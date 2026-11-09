using System.Globalization;
using System.Text;
using System.Text.Json;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Api.Features.Analytics;
using JewelryManager.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// The weekly rhythm: the morning "what matters today", the Friday review with a choice of next week's focus,
/// the quiet month-end sweep, and the monthly duties she defined. All text is built by code from her own
/// numbers, so it costs no tokens and reads the same way every time.
/// </summary>
public class RhythmService(
    AppDbContext db,
    CoachLedger ledger,
    ProactiveOutbox outbox,
    IBackendApiFactory apis,
    IOptions<AgentOptions> options,
    ILogger<RhythmService> log)
{
    public const string FocusPrefix = "f:";

    // ── Morning ───────────────────────────────────────────────────────────────

    public async Task MorningAsync(AgentUser user, CancellationToken ct)
    {
        var local = AgentClock.LocalNow();
        if (!user.MorningSummary || local.Hour < options.Value.Proactive.MorningHour) return;

        // Fridays belong to the weekly review when it is on.
        if (local.DayOfWeek == DayOfWeek.Friday && user.WeeklyReview) return;

        var key = $"morning:{local:yyyy-MM-dd}";
        if (await ledger.FindAsync(user, key, ct) is not null) return;

        var api = apis.CreateFor(user.Id);
        var lines = await TodayLinesAsync(api, user, ct);

        var text = new StringBuilder("בוקר טוב.");
        if (lines.Count == 0) text.Append("\nאין היום משהו דחוף.");
        else
            for (var i = 0; i < Math.Min(3, lines.Count); i++) text.Append($"\n{i + 1}. {lines[i]}");

        if (local.DayOfWeek == DayOfWeek.Sunday && await db.CoachFocuses.AsNoTracking().FirstOrDefaultAsync(f => f.BusinessId == user.BusinessId, ct) is { } focus)
            text.Append($"\n\nהמיקוד השבוע: {FocusArea.Label(focus.Area)}." + (focus.FirstStep is null ? "" : $" צעד קטן לפתיחה: {focus.FirstStep}."));

        if (await outbox.SendAsync(user, text.ToString(), MessageClass.Scheduled, CoachKind.Morning, null, ct, options.Value.Proactive.MorningTemplate))
        {
            var item = await ledger.EnsureAsync(user, key, CoachKind.Morning, "בוקר", ct: ct);
            await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
        }
    }

    private async Task<List<string>> TodayLinesAsync(IBackendApi api, AgentUser user, CancellationToken ct)
    {
        var lines = new List<string>();

        var attention = await TryAsync(() => api.GetAsync("/orders/attention", ct));
        var pendingOrders = Int(attention, "pending");
        if (pendingOrders > 0) lines.Add($"{pendingOrders} הזמנות מהחנות מחכות לאישור");

        var today = AgentClock.LocalToday();
        var tasks = TaskView.ParseList(await TryAsync(() => api.GetAsync("/tasks", ct)))
            .Where(t => !t.IsCompleted && !t.IsAutomatic && t.ParkedAt is null && (t.SnoozedUntil is null || t.SnoozedUntil <= DateTime.UtcNow)
                        && t.DueDate is { } due && due <= today)
            .OrderBy(t => t.DueDate).Take(2);
        foreach (var task in tasks) lines.Add($"משימה להיום: {task.Title}");

        var changes = Int(attention, "alerts");
        if (changes > 0) lines.Add($"{changes} הזמנות שהחנות שינתה");

        var toSend = Int(attention, "messagesToSend");
        if (toSend > 0) lines.Add($"{toSend} סיכומי הזמנה מחכים לשליחה ללקוחות");

        return lines;
    }

    // ── Friday review ─────────────────────────────────────────────────────────

    public async Task FridayAsync(AgentUser user, CancellationToken ct)
    {
        var local = AgentClock.LocalNow();
        if (!user.WeeklyReview || local.DayOfWeek != DayOfWeek.Friday || local.Hour < options.Value.Proactive.MorningHour) return;

        var week = System.Globalization.ISOWeek.GetWeekOfYear(local);
        var weekKey = $"review:{local.Year}-W{week:00}";
        if (await ledger.FindAsync(user, weekKey, ct) is not null) return;

        var api = apis.CreateFor(user.Id);
        var today = AgentClock.LocalToday();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        var weeks = await TryAsync(() => api.GetAsync($"/analytics/sales?from={monday.AddDays(-7):yyyy-MM-dd}&to={today:yyyy-MM-dd}&groupBy=week", ct));
        var (thisOrders, thisRevenue, lastRevenue) = WeekFigures(weeks, monday);

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var month = await TryAsync(() => api.GetAsync($"/finances?from={monthStart:yyyy-MM-dd}&to={today:yyyy-MM-dd}", ct));
        var summary = month.ValueKind == JsonValueKind.Object && month.TryGetProperty("summary", out var s) ? s : default;

        var sellers = await TryAsync(() => api.GetAsync("/analytics/sellers?months=1", ct));
        var best = sellers.ValueKind == JsonValueKind.Object && sellers.TryGetProperty("best", out var b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() > 0
            ? b[0].GetProperty("name").GetString() : null;

        var open = await TryAsync(() => api.GetAsync("/orders?status=active", ct));
        var openCount = open.ValueKind == JsonValueKind.Object && open.TryGetProperty("summary", out var os) ? Int(os, "count") : 0;

        var parked = TaskView.ParseList(await TryAsync(() => api.GetAsync("/tasks", ct))).Count(t => !t.IsCompleted && t.ParkedAt is not null);

        var text = new StringBuilder("סיכום השבוע:");
        text.Append($"\nהכנסות {Money.Format(thisRevenue)} ({thisOrders} הזמנות)" + (lastRevenue > 0 ? $", לעומת {Money.Format(lastRevenue)} בשבוע שעבר." : "."));
        if (summary.ValueKind == JsonValueKind.Object)
            text.Append($"\nהחודש עד עכשיו: הכנסות {Money.Format(Dec(summary, "totalIncome"))}, רווח נקי {Money.Format(Dec(summary, "netProfit"))}.");
        if (best is not null) text.Append($"\nהנמכר ביותר החודש: {best}.");
        text.Append($"\nהזמנות פתוחות: {openCount}.");
        if (parked > 0) text.Append($"\nמשימות בצד: {parked}.");

        IReadOnlyList<ReplyButton>? buttons = null;
        var offered = new List<FocusOption>();
        if (user.CoachEnabled)
        {
            offered = await FocusOptionsAsync(user, api, summary, ct);
            if (offered.Count > 0)
            {
                text.Append("\n\nמה המיקוד לשבוע הבא?");
                for (var i = 0; i < offered.Count; i++) text.Append($"\n{i + 1}. {FocusArea.Label(offered[i].Area)}: {offered[i].Reason}");
                buttons = offered.Select(o => new ReplyButton(FocusPrefix + o.Area, FocusArea.Label(o.Area))).ToList();
            }
        }

        if (!await outbox.SendAsync(user, text.ToString(), MessageClass.Scheduled, CoachKind.Review, buttons, ct, options.Value.Proactive.MorningTemplate)) return;

        var review = await ledger.EnsureAsync(user, weekKey, CoachKind.Review, "סיכום שבועי", ct: ct);
        await ledger.SetStateAsync(review, CoachState.Done, ct: ct);
        foreach (var option in offered)
            await ledger.EnsureAsync(user, $"growth:{option.Area}:{local.Year}-W{week:00}", CoachKind.Growth, $"{FocusArea.Label(option.Area)}: {option.FirstStep}", ct: ct);
    }

    private async Task<List<FocusOption>> FocusOptionsAsync(AgentUser user, IBackendApi api, JsonElement financeSummary, CancellationToken ct)
    {
        var growthJson = await TryAsync(() => api.GetAsync("/analytics/growth", ct));
        if (growthJson.ValueKind != JsonValueKind.Object) return [];

        var snapshot = JsonSerializer.Deserialize<GrowthSnapshot>(growthJson.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (snapshot is null) return [];

        var today = AgentClock.LocalToday();
        var enabled = await db.CoachOccasions.AsNoTracking().Where(o => o.BusinessId == user.BusinessId && o.Enabled).Select(o => o.Code).ToListAsync(ct);
        (string, int)? next = enabled
            .Select(c => (Name: OccasionCalendar.Find(c)?.Name ?? c, Days: OccasionCalendar.NextOnOrAfter(c, today).DayNumber - today.DayNumber))
            .OrderBy(x => x.Days).Select(x => ((string, int)?)x).FirstOrDefault();

        var sleeping = new HashSet<string>();
        foreach (var area in new[] { FocusArea.Advertising, FocusArea.NewJewelry, FocusArea.Website, FocusArea.Customers, FocusArea.Admin, FocusArea.Occasion })
            if (await ledger.RecentRefusalsAsync(user, $"growth:{area}:", 60, ct) >= 3) sleeping.Add(area);

        var admin = Int(financeSummary, "expensesWithoutReceipt");
        return GrowthAdvisor.Options(snapshot, admin, next, sleeping);
    }

    public async Task<AgentReply?> HandleFocusButtonAsync(AgentUser user, string buttonId, CancellationToken ct)
    {
        if (!buttonId.StartsWith(FocusPrefix, StringComparison.Ordinal)) return null;
        var area = buttonId[FocusPrefix.Length..];

        var week = System.Globalization.ISOWeek.GetWeekOfYear(AgentClock.LocalNow());
        var year = AgentClock.LocalNow().Year;
        var chosen = await ledger.FindAsync(user, $"growth:{area}:{year}-W{week:00}", ct);

        var reason = chosen?.Title ?? FocusArea.Label(area);
        var step = chosen is not null && chosen.Title.Contains(':') ? chosen.Title[(chosen.Title.IndexOf(':') + 1)..].Trim() : null;

        var existing = await db.CoachFocuses.FirstOrDefaultAsync(f => f.BusinessId == user.BusinessId, ct);
        if (existing is null)
            db.CoachFocuses.Add(new CoachFocus { Id = Guid.NewGuid(), BusinessId = user.BusinessId, Area = area, Reason = reason, FirstStep = step, StartedAt = DateTime.UtcNow });
        else
        {
            existing.Area = area;
            existing.Reason = reason;
            existing.FirstStep = step;
            existing.StartedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        if (chosen is not null) await ledger.SetStateAsync(chosen, CoachState.Accepted, ct: ct);

        // The two not chosen are simply not for this week; they are not counted as refusals.
        return new AgentReply($"מעולה. המיקוד לשבוע הבא: {FocusArea.Label(area)}." + (step is null ? "" : $" צעד קטן לפתיחה: {step}."));
    }

    public static (int Orders, decimal Revenue, decimal LastWeekRevenue) WeekFigures(JsonElement rows, DateOnly monday)
    {
        if (rows.ValueKind != JsonValueKind.Array) return (0, 0, 0);

        int orders = 0;
        decimal thisRevenue = 0, lastRevenue = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (!DateOnly.TryParse(row.GetProperty("period").GetString(), CultureInfo.InvariantCulture, out var start)) continue;
            if (start == monday) { orders = Int(row, "orders"); thisRevenue = Dec(row, "revenue"); }
            else if (start == monday.AddDays(-7)) lastRevenue = Dec(row, "revenue");
        }

        return (orders, thisRevenue, lastRevenue);
    }

    // ── Month end, quietly ────────────────────────────────────────────────────

    public async Task MonthEndAsync(AgentUser user, CancellationToken ct)
    {
        var today = AgentClock.LocalToday();
        var lastDay = DateTime.DaysInMonth(today.Year, today.Month);
        if (!user.CoachEnabled || today.Day < lastDay - 2 || AgentClock.LocalNow().Hour < options.Value.Proactive.MorningHour) return;

        var month = $"{today:yyyy-MM}";
        var key = $"monthend:{month}";
        if (await ledger.FindAsync(user, key, ct) is not null) return;

        var api = apis.CreateFor(user.Id);
        var finances = await TryAsync(() => api.GetAsync($"/finances?from={month}-01&to={today:yyyy-MM-dd}", ct));
        var withoutReceipt = finances.ValueKind == JsonValueKind.Object && finances.TryGetProperty("summary", out var s) ? Int(s, "expensesWithoutReceipt") : 0;

        var statement = await TryAsync(() => api.GetAsync($"/statements/months/{month}", ct));
        var pendingLines = Int(statement, "pendingCount");

        var completed = await TryAsync(() => api.GetAsync($"/orders?status=completed&from={month}-01&to={today:yyyy-MM-dd}", ct));
        var noReceipt = completed.ValueKind == JsonValueKind.Object && completed.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array
            ? orders.EnumerateArray().Count(o => o.TryGetProperty("receiptSent", out var r) && r.ValueKind == JsonValueKind.False)
            : 0;

        // Nothing to tidy: silence is the right message.
        var parts = new List<string>();
        if (withoutReceipt > 0) parts.Add($"{withoutReceipt} הוצאות בלי קבלה");
        if (pendingLines > 0) parts.Add($"{pendingLines} שורות בנק שלא הותאמו");
        if (noReceipt > 0) parts.Add($"{noReceipt} הזמנות שהושלמו בלי שנשלחה קבלה");

        var item = await ledger.EnsureAsync(user, key, CoachKind.MonthEnd, $"סוף חודש {month}", ct: ct);
        if (parts.Count == 0)
        {
            await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
            return;
        }

        var text = "לקראת סוף החודש, כמה דברים קטנים לסגור: " + string.Join(", ", parts) + ". אם תרצי, אראה לך כל אחד.";
        if (await outbox.SendAsync(user, text, MessageClass.Scheduled, CoachKind.MonthEnd, null, ct))
            await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
        else
            db.Entry(item).State = EntityState.Detached;
    }

    // ── Duties she defined ────────────────────────────────────────────────────

    public async Task ObligationsAsync(AgentUser user, CancellationToken ct)
    {
        var today = AgentClock.LocalToday();
        var month = $"{today:yyyy-MM}";
        var due = await db.RecurringObligations
            .Where(o => o.BusinessId == user.BusinessId && o.IsActive && o.LastCreatedMonth != month)
            .ToListAsync(ct);

        var api = apis.CreateFor(user.Id);
        foreach (var obligation in due)
        {
            var day = Math.Min(obligation.DayOfMonth, DateTime.DaysInMonth(today.Year, today.Month));
            if (today.Day < day) continue;

            try
            {
                var body = new { title = obligation.Title, status = "NEW", dueDate = new DateOnly(today.Year, today.Month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                await api.SendAsync(HttpMethod.Post, "/tasks", body, ct);
                obligation.LastCreatedMonth = month;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is BackendApiException or HttpRequestException)
            {
                log.LogWarning(ex, "Could not create the monthly task '{Title}'", obligation.Title);
            }
        }
    }

    public async Task<string?> AddObligationAsync(AgentUser user, string title, int dayOfMonth, CancellationToken ct)
    {
        var clean = PreferenceRules.Clean(title);
        if (clean.Length is < 3 or > 200) return "הכותרת לא תקינה";
        if (dayOfMonth is < 1 or > 31) return "היום בחודש חייב להיות בין 1 ל-31";
        if (await db.RecurringObligations.CountAsync(o => o.BusinessId == user.BusinessId && o.IsActive, ct) >= 15) return "יש כבר יותר מדי חובות חוזרות";

        db.RecurringObligations.Add(new RecurringObligation { Id = Guid.NewGuid(), BusinessId = user.BusinessId, Title = clean, DayOfMonth = dayOfMonth });
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<bool> RemoveObligationAsync(AgentUser user, string title, CancellationToken ct)
    {
        var clean = PreferenceRules.Clean(title);
        return await db.RecurringObligations.Where(o => o.BusinessId == user.BusinessId && o.IsActive && o.Title.Contains(clean))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsActive, false), ct) > 0;
    }

    public Task<List<RecurringObligation>> ListObligationsAsync(AgentUser user, CancellationToken ct) =>
        db.RecurringObligations.AsNoTracking().Where(o => o.BusinessId == user.BusinessId && o.IsActive).OrderBy(o => o.DayOfMonth).ToListAsync(ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    // A failed read means "no information", never a wrong message.
    private static async Task<JsonElement> TryAsync(Func<Task<JsonElement>> read)
    {
        try { return await read(); }
        catch (Exception ex) when (ex is BackendApiException or HttpRequestException or TaskCanceledException) { return default; }
    }

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static decimal Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : 0;
}
