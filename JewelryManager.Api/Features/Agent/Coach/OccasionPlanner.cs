using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Mcp;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Helps her prepare for the occasions that matter to her business: a question weeks ahead, an offer that stays
/// above her profit floor with advertising text, a checklist as tasks, and one line about how it went.
/// Which occasions count is learned from her own past orders, and she can change the list at any time.
/// </summary>
public class OccasionPlanner(
    AppDbContext db,
    CoachLedger ledger,
    ProactiveOutbox outbox,
    IBackendApiFactory apis,
    IAgentModel model,
    UsageService usage,
    PreferenceService preferences,
    PendingActionService pending,
    ILogger<OccasionPlanner> log)
{
    public const string Prefix = "o:";

    public static readonly string[] Checklist =
    [
        "לבחור את המוצרים שיהיו במבצע",
        "לצלם או לעדכן תמונות של המוצרים",
        "לוודא מלאי של חומרים ואריזות",
        "להכין פוסט והודעה ללקוחות",
        "לפרסם את המבצע",
    ];

    public async Task RunAsync(AgentUser user, CancellationToken ct)
    {
        if (!user.CoachEnabled) return;

        var api = apis.CreateFor(user.Id);
        var today = AgentClock.LocalToday();
        var days = await SalesByDayAsync(api, today, ct);
        var enabled = await EnabledAsync(user, days, today, ct);

        foreach (var code in enabled)
        {
            var occasion = OccasionCalendar.Find(code);
            if (occasion is null) continue;

            var date = OccasionCalendar.NextOnOrAfter(code, today.AddDays(-10));
            var daysUntil = date.DayNumber - today.DayNumber;
            var key = $"season:{code}:{date.Year}";
            var item = await ledger.FindAsync(user, key, ct);

            var step = OccasionTimeline.Decide(daysUntil, item?.Stage ?? 0, item?.State ?? CoachState.Suggested,
                item?.MentionCount ?? 0, item?.LastMentionedAt, DateTime.UtcNow);
            if (step == OccasionStep.None) continue;

            item ??= await ledger.EnsureAsync(user, key, CoachKind.Season, $"{occasion.Name} {date.Year}", ct: ct);
            if (await SpeakAsync(user, item, occasion, step, daysUntil, date, days, ct)) return;
        }
    }

    private async Task<bool> SpeakAsync(AgentUser user, CoachItem item, Occasion occasion, OccasionStep step, int daysUntil,
        DateOnly date, List<(DateOnly Date, int Orders)> days, CancellationToken ct)
    {
        string text;
        List<ReplyButton> buttons;
        int? newStage = null;

        switch (step)
        {
            case OccasionStep.Ask or OccasionStep.Remind:
                var lastYear = OrdersBefore(days, date.AddYears(-1));
                text = step == OccasionStep.Ask
                    ? $"{occasion.Name} בעוד {daysUntil} ימים." + (lastYear > 0 ? $" בשנה שעברה היו {lastYear} הזמנות בשלושת השבועות שלפני." : "") + " לתכנן מבצע ופרסום מראש?"
                    : $"ממשיכה לחשוב על {occasion.Name}. לתכנן מבצע?";
                buttons = [new($"{Prefix}y:{item.Id}", "כן, בואי נתכנן"), new($"{Prefix}n:{item.Id}", "לא השנה"), new($"{Prefix}l:{item.Id}", "אחר כך")];
                newStage = 1;
                break;

            case OccasionStep.Checklist:
                text = $"נשארו {daysUntil} ימים ל{occasion.Name}. להוסיף למשימות רשימת הכנות?";
                buttons = [new($"{Prefix}t:{item.Id}", "כן, תוסיפי"), new($"{Prefix}d:{item.Id}", "לא צריך")];
                newStage = 3;
                break;

            default:
                var now = OrdersBefore(days, date);
                var before = OrdersBefore(days, date.AddYears(-1));
                text = $"{occasion.Name} עבר. בשלושת השבועות שלפניו היו {now} הזמנות" + (before > 0 ? $", לעומת {before} בשנה שעברה." : ".");
                buttons = [];
                newStage = 4;
                break;
        }

        if (!await outbox.SendAsync(user, text, MessageClass.Coaching, CoachKind.Season, buttons, ct)) return false;

        if (newStage is { } stage) item.Stage = stage;
        await ledger.MentionedAsync(item, ct);
        if (step == OccasionStep.Results) await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
        return true;
    }

    // ── Her answers ───────────────────────────────────────────────────────────

    public async Task<AgentReply?> HandleButtonAsync(AgentUser user, string buttonId, CancellationToken ct)
    {
        if (!buttonId.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = buttonId[Prefix.Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var itemId)) return new AgentReply("לא הבנתי את הלחיצה.");

        var item = await ledger.GetAsync(user, itemId, ct);
        if (item is null) return new AgentReply("זה כבר לא רלוונטי.");
        var code = item.Key.Split(':').ElementAtOrDefault(1) ?? "";
        var occasion = OccasionCalendar.Find(code);

        switch (parts[0])
        {
            case "y":
                await ledger.SetStateAsync(item, CoachState.Accepted, stage: 2, ct: ct);
                return new AgentReply(await ProposalAsync(user, occasion?.Name ?? item.Title, ct));

            case "n":
                await ledger.SetStateAsync(item, CoachState.Declined, ct: ct);
                return new AgentReply("בסדר, לא השנה.");

            case "l":
                await ledger.SetStateAsync(item, CoachState.Snoozed, snoozedUntil: DateTime.UtcNow.AddDays(14), ct: ct);
                return new AgentReply("אזכיר בעוד שבועיים.");

            case "t":
                var summary = "להוסיף למשימות את רשימת ההכנות ל" + (occasion?.Name ?? "מועד") + "?\n" + string.Join("\n", Checklist.Select((s, i) => $"{i + 1}. {s}"));
                var args = JsonSerializer.Serialize(new { steps = Checklist, summary });
                var action = await pending.CreateAsync(user, "add_tasks", args, summary, ct);
                return new AgentReply(summary, action.Id);

            case "d":
                return new AgentReply("בסדר.");

            default:
                return new AgentReply("לא הבנתי את הלחיצה.");
        }
    }

    /// <summary>The offer for an occasion, kept above her profit floor, with advertising text worded by the model.</summary>
    private async Task<string> ProposalAsync(AgentUser user, string occasionName, CancellationToken ct)
    {
        var offer = await OfferLineAsync(user, ct);
        var draft = await AdDraftAsync(user, occasionName, offer, ct);
        return $"הצעה ל{occasionName}:\n{offer}" + (draft is null ? "" : $"\n\nטיוטה לפוסט:\n{draft}");
    }

    private async Task<string> OfferLineAsync(AgentUser user, CancellationToken ct)
    {
        try
        {
            var data = await apis.CreateFor(user.Id).GetAsync("/analytics/discount-ceiling", ct);
            var rows = data.TryGetProperty("byType", out var by) && by.ValueKind == JsonValueKind.Array
                ? by.EnumerateArray().Select(r => (Type: r.GetProperty("type").GetString() ?? "", Safe: r.GetProperty("safeForAllPercent").GetDecimal(), Median: r.GetProperty("medianPercent").GetDecimal()))
                    .Where(r => r.Safe >= 5).OrderByDescending(r => r.Safe).Take(3).ToList()
                : [];

            return rows.Count == 0
                ? "המרווחים צמודים, אז במקום הנחה אפשר להוסיף מתנה קטנה (אריזה או שרשרת) בלי לרדת מרצפת הרווח."
                : "הנחות שבטוחות מול רצפת הרווח: " + string.Join(", ", rows.Select(r => $"{r.Type} עד {Math.Floor(r.Safe):0}%")) + ".";
        }
        catch (Exception ex) when (ex is BackendApiException or HttpRequestException)
        {
            log.LogWarning(ex, "Could not read the discount ceiling");
            return "אפשר להציע מבצע קטן, ואבדוק איתך את המרווח לפני שמפרסמים.";
        }
    }

    private async Task<string?> AdDraftAsync(AgentUser user, string occasionName, string offer, CancellationToken ct)
    {
        var tone = string.Join("\n", (await preferences.ListAsync(user, ct)).Select(p => "- " + p.Instruction));
        var system = "Write a short Hebrew Instagram caption (3 lines) and one story line for a handmade jewelry business, for the occasion given. " +
                     "Warm, specific, no exaggerated claims, no invented prices. Mention the offer only in general terms. Answer with the text only.";
        var request = new ModelRequest(system, tone.Length > 0 ? "Her style preferences:\n" + tone : "", [],
            [ChatMessage.User($"Occasion: {occasionName}\nOffer idea: {offer}")]);

        try
        {
            var turn = await model.CompleteAsync(request, ct);
            await usage.RecordAsync(user, model.ModelName, turn.Usage, ct);
            return string.IsNullOrWhiteSpace(turn.Text) ? null : turn.Text.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            log.LogWarning(ex, "The ad draft could not be written");
            return null;
        }
    }

    // ── Which occasions count ─────────────────────────────────────────────────

    private async Task<List<string>> EnabledAsync(AgentUser user, List<(DateOnly Date, int Orders)> days, DateOnly today, CancellationToken ct)
    {
        var rows = await db.CoachOccasions.Where(o => o.BusinessId == user.BusinessId).ToListAsync(ct);
        if (rows.Count == 0)
        {
            foreach (var code in OccasionCalendar.DefaultChoice(days, today))
                db.CoachOccasions.Add(new CoachOccasion { Id = Guid.NewGuid(), BusinessId = user.BusinessId, Code = code, Enabled = true, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            rows = await db.CoachOccasions.Where(o => o.BusinessId == user.BusinessId).ToListAsync(ct);
        }

        return rows.Where(r => r.Enabled).Select(r => r.Code).ToList();
    }

    public async Task<string?> SetAsync(AgentUser user, string code, bool enabled, CancellationToken ct)
    {
        if (OccasionCalendar.Find(code) is null) return "unknown occasion; use one of: " + string.Join(", ", OccasionCalendar.All.Select(o => o.Code));
        var row = await db.CoachOccasions.FirstOrDefaultAsync(o => o.BusinessId == user.BusinessId && o.Code == code, ct);
        if (row is null) db.CoachOccasions.Add(new CoachOccasion { Id = Guid.NewGuid(), BusinessId = user.BusinessId, Code = code, Enabled = enabled, UpdatedAt = DateTime.UtcNow });
        else { row.Enabled = enabled; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static async Task<List<(DateOnly Date, int Orders)>> SalesByDayAsync(IBackendApi api, DateOnly today, CancellationToken ct)
    {
        try
        {
            var data = await api.GetAsync($"/analytics/sales?from={today.AddYears(-5):yyyy-MM-dd}&to={today:yyyy-MM-dd}&groupBy=day", ct);
            return data.ValueKind != JsonValueKind.Array
                ? []
                : data.EnumerateArray().Select(r => (DateOnly.Parse(r.GetProperty("period").GetString()!, CultureInfo.InvariantCulture), r.GetProperty("orders").GetInt32())).ToList();
        }
        catch (Exception ex) when (ex is BackendApiException or HttpRequestException)
        {
            return [];
        }
    }

    private static int OrdersBefore(List<(DateOnly Date, int Orders)> days, DateOnly date) =>
        days.Where(d => d.Date >= date.AddDays(-21) && d.Date < date).Sum(d => d.Orders);
}
