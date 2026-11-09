using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Watches the gold and silver market once a day and tells her when it has moved enough to be worth a look at
/// the prices in her settings. Built to be trusted: two independent sources must agree, an alert needs two days,
/// every reading is kept, a baseline resets itself when she changes a price, and for the first weeks it only records.
/// The agent never changes a setting.
/// </summary>
public class MetalIndexService(
    AppDbContext db,
    MetalSources sources,
    IBackendApiFactory apis,
    ProactiveOutbox outbox,
    ProactiveLog proactiveLog,
    IWhatsAppClient whatsapp,
    IOptions<AgentOptions> options,
    ILogger<MetalIndexService> log)
{
    public const string Prefix = "m:";
    private static readonly string[] Metals = ["gold", "silver"];

    private MetalsOptions Config => options.Value.Metals;

    /// <summary>One daily round for every business that has the agent. Safe to call every few minutes.</summary>
    public async Task RunDailyAsync(IReadOnlyList<AgentUser> users, CancellationToken ct)
    {
        if (!Config.Enabled || users.Count == 0 || AgentClock.LocalNow().Hour < Config.Hour) return;

        var todayStart = AgentClock.StartOfTodayUtc();
        var acceptedToday = await db.MetalPriceReadings.AsNoTracking().Where(r => r.At >= todayStart && r.Accepted).Select(r => r.Metal).Distinct().ToListAsync(ct);
        var attemptsToday = await db.MetalPriceReadings.AsNoTracking().CountAsync(r => r.At >= todayStart && !r.Accepted, ct);

        if (acceptedToday.Count < Metals.Length && attemptsToday < 8)
            await FetchAsync(ct);

        await WarnDevelopersIfStaleAsync(users, ct);

        var spots = await LatestSpotsAsync(ct);
        if (spots.Count == 0) return;

        foreach (var business in users.GroupBy(u => u.BusinessId))
        {
            try { await ProcessBusinessAsync(business.First(), spots, ct); }
            catch (Exception ex) when (ex is BackendApiException or HttpRequestException)
            {
                log.LogWarning(ex, "The metal index could not be updated for a business");
            }
        }
    }

    /// <summary>Reads each metal from every source, keeps only what at least two sources agree on, and records everything.</summary>
    public async Task FetchAsync(CancellationToken ct)
    {
        var fx = await sources.UsdIlsAsync(ct);
        foreach (var metal in Metals)
        {
            var readings = new List<SourceReading>();
            foreach (var source in Config.Sources) readings.Add(await sources.ReadAsync(source, metal, ct));

            var values = readings.Where(r => r.UsdPerOunce is not null).Select(r => r.UsdPerOunce!.Value).ToList();
            var agreed = fx is null || values.Count < 2 ? null : MetalMath.Consensus(values, Config.AgreementPercent);

            foreach (var reading in readings)
                db.MetalPriceReadings.Add(new MetalPriceReading
                {
                    Id = Guid.NewGuid(), Metal = metal, Source = reading.Source, UsdPerOunce = reading.UsdPerOunce ?? 0, UsdIls = fx ?? 0,
                    IlsPerGram = reading.UsdPerOunce is { } p && fx is { } rate ? MetalMath.IlsPerGram(p, rate) : 0,
                    Accepted = false, Note = reading.Problem, At = DateTime.UtcNow,
                });

            if (agreed is { } mid && fx is { } usdIls)
            {
                db.MetalPriceReadings.Add(new MetalPriceReading
                {
                    Id = Guid.NewGuid(), Metal = metal, Source = "agreed:" + string.Join("+", readings.Where(r => r.UsdPerOunce is not null).Select(r => r.Source)),
                    UsdPerOunce = mid, UsdIls = usdIls, IlsPerGram = MetalMath.IlsPerGram(mid, usdIls), Accepted = true, At = DateTime.UtcNow,
                });
            }
            else
            {
                db.MetalPriceReadings.Add(new MetalPriceReading
                {
                    Id = Guid.NewGuid(), Metal = metal, Source = "rejected", Accepted = false, At = DateTime.UtcNow,
                    Note = fx is null ? "no exchange rate" : values.Count < 2 ? "fewer than two sources answered" : "the sources disagree",
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<string, (decimal IlsPerGram, DateTime At, string Source)>> LatestSpotsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, (decimal, DateTime, string)>();
        foreach (var metal in Metals)
        {
            var latest = await db.MetalPriceReadings.AsNoTracking().Where(r => r.Metal == metal && r.Accepted)
                .OrderByDescending(r => r.At).FirstOrDefaultAsync(ct);
            if (latest is not null && latest.At >= DateTime.UtcNow.AddDays(-3)) result[metal] = (latest.IlsPerGram, latest.At, latest.Source);
        }

        return result;
    }

    private async Task ProcessBusinessAsync(AgentUser user, Dictionary<string, (decimal IlsPerGram, DateTime At, string Source)> spots, CancellationToken ct)
    {
        var api = apis.CreateFor(user.Id);
        var materials = await api.GetAsync("/analytics/materials", ct);
        if (materials.ValueKind != JsonValueKind.Array) return;

        var firstReading = await db.MetalPriceReadings.AsNoTracking().Where(r => r.Accepted).MinAsync(r => (DateTime?)r.At, ct) ?? DateTime.UtcNow;
        var shadow = DateTime.UtcNow < firstReading.AddDays(Config.ShadowDays);
        var today = AgentClock.LocalToday();

        var existing = await db.MetalMappings.Where(m => m.BusinessId == user.BusinessId).ToListAsync(ct);
        var crossings = new Dictionary<string, (decimal Change, MetalMapping Map)>();

        foreach (var row in materials.EnumerateArray())
        {
            var name = row.GetProperty("name").GetString() ?? "";
            var price = row.GetProperty("pricePerGram").GetDecimal();

            var map = existing.FirstOrDefault(m => m.MaterialName == name);
            if (map is null)
            {
                if (MetalMath.Guess(name) is not { } guess) continue;
                map = new MetalMapping { Id = Guid.NewGuid(), BusinessId = user.BusinessId, MaterialName = name, Metal = guess.Metal, Purity = guess.Purity };
                db.MetalMappings.Add(map);
                existing.Add(map);
            }

            if (!spots.TryGetValue(map.Metal, out var spot)) continue;

            // First sight, or she changed the price: her prices are true as of today, so the baseline is today's market.
            if (map.LastSettingsPrice == 0 || map.LastSettingsPrice != price)
            {
                var oldPremium = map.ImpliedPremium;
                var newPremium = MetalMath.Premium(price, spot.IlsPerGram, map.Purity);
                if (oldPremium > 0 && newPremium > 0 && Math.Abs(newPremium - oldPremium) / oldPremium > 0.03m)
                    await TellDevelopersAsync($"פרמיית {name} השתנתה מ-{oldPremium:0.000} ל-{newPremium:0.000} בעדכון מחיר. המדד אולי לא עוקב אחרי הספק.", $"premium:{map.Id}:{today:yyyy-MM-dd}", ct);

                map.LastSettingsPrice = price;
                map.BaselineSpotIlsPerGram = spot.IlsPerGram;
                map.ImpliedPremium = newPremium;
                map.BaselineAt = DateTime.UtcNow;
                map.FirstCrossedOn = null;
                map.AlertedOn = null;
                continue;
            }

            var change = MetalMath.ChangePercent(spot.IlsPerGram, map.BaselineSpotIlsPerGram);
            var threshold = map.Metal == "gold" ? Config.GoldThresholdPercent : Config.SilverThresholdPercent;
            switch (MetalMath.Evaluate(change, threshold, map.FirstCrossedOn, map.AlertedOn, today))
            {
                case IndexAction.MarkCrossing: map.FirstCrossedOn = today; break;
                case IndexAction.ClearCrossing: map.FirstCrossedOn = null; map.AlertedOn = null; break;
                case IndexAction.Alert:
                    if (!crossings.TryGetValue(map.Metal, out var seen) || Math.Abs(change) > Math.Abs(seen.Change)) crossings[map.Metal] = (change, map);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        if (shadow) return;

        foreach (var (metal, (change, map)) in crossings)
        {
            var spot = spots[metal];
            var expected = Math.Round(map.LastSettingsPrice * (1 + change / 100), 2);
            var text = $"מדד {MetalMath.MetalName(metal)} {(change > 0 ? "עלה" : "ירד")} {Math.Abs(change):0.#}% מאז העדכון האחרון בהגדרות " +
                       $"(לפי שני מקורות, {AgentClock.ToLocal(spot.At):dd/MM HH:mm}). מחיר צפוי לגרם ב{map.MaterialName}: כ-{expected:0.##} ₪ במקום {map.LastSettingsPrice:0.##}. לחשב מה זה עושה למחירים?";
            var buttons = new List<ReplyButton> { new($"{Prefix}i:{metal}", "כן, תחשבי"), new($"{Prefix}x:{metal}", "התעלמי") };

            if (await outbox.SendAsync(user, text, MessageClass.Scheduled, CoachKind.Metal, buttons, ct))
                foreach (var m in existing.Where(m => m.Metal == metal)) m.AlertedOn = today;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Her answer ────────────────────────────────────────────────────────────

    public async Task<AgentReply?> HandleButtonAsync(AgentUser user, string buttonId, CancellationToken ct)
    {
        if (!buttonId.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = buttonId[Prefix.Length..].Split(':', 2);
        if (parts.Length != 2 || parts[1] is not ("gold" or "silver")) return new AgentReply("לא הבנתי את הלחיצה.");

        var metal = parts[1];
        var spots = await LatestSpotsAsync(ct);
        if (!spots.TryGetValue(metal, out var spot)) return new AgentReply("אין לי כרגע קריאה עדכנית של השוק.");

        var maps = await db.MetalMappings.Where(m => m.BusinessId == user.BusinessId && m.Metal == metal).ToListAsync(ct);
        if (maps.Count == 0) return new AgentReply("לא מצאתי חומרים מסוג הזה בהגדרות.");

        if (parts[0] == "x")
        {
            // She accepts the move: today's market becomes the new baseline.
            foreach (var map in maps) { map.BaselineSpotIlsPerGram = spot.IlsPerGram; map.FirstCrossedOn = null; map.AlertedOn = null; map.BaselineAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
            return new AgentReply("בסדר, אתעלם מהשינוי הזה ואמשיך לעקוב מהמחיר של היום.");
        }

        var changes = maps.Select(m => new
        {
            material = m.MaterialName,
            newPricePerGram = Math.Round(m.LastSettingsPrice * (1 + MetalMath.ChangePercent(spot.IlsPerGram, m.BaselineSpotIlsPerGram) / 100), 4),
        }).ToList();

        var result = await apis.CreateFor(user.Id).SendAsync(HttpMethod.Post, "/analytics/what-if", new { changes }, ct);
        return new AgentReply(DescribeWhatIf(result, maps.Select(m => m.MaterialName).ToList()));
    }

    public static string DescribeWhatIf(JsonElement result, IReadOnlyList<string> materials)
    {
        var products = result.TryGetProperty("products", out var p) ? p.GetInt32() : 0;
        if (products == 0) return "אין מוצרים שהשינוי הזה משפיע עליהם.";

        var average = result.GetProperty("averageChangePercent").GetDecimal();
        var below = result.GetProperty("newlyBelowProfitFloor").GetInt32();
        var text = $"אם המחירים של {string.Join(", ", materials)} בהגדרות יתעדכנו לפי השוק: המחיר המומלץ ישתנה בממוצע {average:+0.#;-0.#;0}% על פני {products} מוצרים.";

        if (result.TryGetProperty("byType", out var types) && types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0)
        {
            var top = types.EnumerateArray().Take(3).Select(t => $"{t.GetProperty("type").GetString()} {t.GetProperty("averageChangePercent").GetDecimal():+0.#;-0.#;0}%");
            text += "\nלפי סוג: " + string.Join(", ", top) + ".";
        }

        if (below > 0)
        {
            var examples = result.TryGetProperty("newlyBelowExamples", out var ex) ? string.Join(", ", ex.EnumerateArray().Select(e => e.GetString())) : "";
            text += $"\n{below} מוצרים יירדו מתחת לרצפת הרווח במחיר האתר הנוכחי" + (examples.Length > 0 ? $" (למשל: {examples})" : "") + ".";
        }

        return text + "\nאת מעדכנת את המחירים בהגדרות באפליקציה; אני לא משנה אותם.";
    }

    // ── Failures are for the developer ────────────────────────────────────────

    private async Task WarnDevelopersIfStaleAsync(IReadOnlyList<AgentUser> users, CancellationToken ct)
    {
        var last = await db.MetalPriceReadings.AsNoTracking().Where(r => r.Accepted).MaxAsync(r => (DateTime?)r.At, ct);
        if (last is { } at && at >= DateTime.UtcNow.AddDays(-3)) return;

        // Never read at all is only worth a warning once the index has been switched on for a few days.
        var anyReading = await db.MetalPriceReadings.AsNoTracking().MinAsync(r => (DateTime?)r.At, ct);
        if (last is null && (anyReading is null || anyReading > DateTime.UtcNow.AddDays(-3))) return;

        await TellDevelopersAsync("שער הזהב והכסף לא התקבל כבר 3 ימים (המקורות לא עונים או לא מסכימים). בדוק את ההגדרות של מקורות המחיר.", $"metal-stale:{AgentClock.LocalToday():yyyy-MM-dd}", ct);
    }

    private async Task TellDevelopersAsync(string text, string key, CancellationToken ct)
    {
        var developers = await db.AgentUsers.AsNoTracking().Where(u => u.IsActive && u.IsDeveloper).ToListAsync(ct);
        foreach (var developer in developers)
        {
            if (!await proactiveLog.TryClaimAsync(developer.Id, "metal_dev", key, ct)) continue;
            try { await whatsapp.SendTextAsync(developer.Phone, text, ct); }
            catch (HttpRequestException ex) { log.LogWarning(ex, "Could not alert the developer"); }
        }
    }

    public async Task<string?> MapMaterialAsync(AgentUser user, string material, string metal, decimal purity, CancellationToken ct)
    {
        if (metal is not ("gold" or "silver")) return "metal must be gold or silver";
        if (purity is <= 0.3m or > 1m) return "purity must be a fraction such as 0.585";

        var row = await db.MetalMappings.FirstOrDefaultAsync(m => m.BusinessId == user.BusinessId && m.MaterialName == material, ct);
        if (row is null) db.MetalMappings.Add(new MetalMapping { Id = Guid.NewGuid(), BusinessId = user.BusinessId, MaterialName = material, Metal = metal, Purity = purity });
        else { row.Metal = metal; row.Purity = purity; row.LastSettingsPrice = 0; }
        await db.SaveChangesAsync(ct);
        return null;
    }
}
