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
/// Customers worth a kind word: those who ordered about a year ago. The agent offers to word a short thank-you
/// or anniversary note; she sends it herself. The agent only ever sees names, never phone numbers.
/// </summary>
public class AnniversaryCoach(
    CoachLedger ledger,
    ProactiveOutbox outbox,
    IBackendApiFactory apis,
    IAgentModel model,
    UsageService usage,
    PreferenceService preferences,
    ILogger<AnniversaryCoach> log)
{
    public const string Prefix = "a:";

    public async Task RunAsync(AgentUser user, CancellationToken ct)
    {
        // Once a week is plenty (Wednesdays); the gate still decides whether today is a good day to speak.
        if (!user.CoachEnabled || AgentClock.LocalNow().DayOfWeek != DayOfWeek.Wednesday) return;

        List<(int Number, string Customer, string? Item)> rows;
        try
        {
            var data = await apis.CreateFor(user.Id).GetAsync("/analytics/anniversaries?daysBack=365&window=7", ct);
            rows = data.ValueKind != JsonValueKind.Array ? [] : data.EnumerateArray()
                .Select(r => (r.GetProperty("orderNumber").GetInt32(), r.GetProperty("customer").GetString() ?? "",
                    r.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null)).ToList();
        }
        catch (Exception ex) when (ex is BackendApiException or HttpRequestException)
        {
            log.LogWarning(ex, "Could not read last year's customers");
            return;
        }

        // Each order is offered at most once, ever.
        var fresh = new List<(int Number, string Customer, string? Item)>();
        foreach (var row in rows)
            if (await ledger.FindAsync(user, $"anniv-seen:{row.Number}", ct) is null) fresh.Add(row);
        fresh = fresh.Where(r => r.Customer.Length > 0).Take(3).ToList();
        if (fresh.Count == 0) return;

        var names = string.Join(", ", fresh.Select(r => r.Item is null ? r.Customer : $"{r.Customer} ({r.Item})"));
        var item = await ledger.OfferAsync(user, $"anniversary:{fresh[0].Number}", CoachKind.Anniversary, names, ct: ct);
        if (item is null) return;

        var text = $"לפני שנה בערך הזמינו אצלך: {names}. רוצה שאנסח הודעה קצרה של תודה או איחול?";
        var buttons = new List<ReplyButton> { new($"{Prefix}y:{item.Id}", "כן, תנסחי"), new($"{Prefix}n:{item.Id}", "לא עכשיו") };
        if (!await outbox.SendAsync(user, text, MessageClass.Coaching, CoachKind.Anniversary, buttons, ct)) return;

        await ledger.MentionedAsync(item, ct);
        foreach (var row in fresh) await ledger.EnsureAsync(user, $"anniv-seen:{row.Number}", CoachKind.System, row.Customer, ct: ct);
    }

    public async Task<AgentReply?> HandleButtonAsync(AgentUser user, string buttonId, CancellationToken ct)
    {
        if (!buttonId.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = buttonId[Prefix.Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id) || await ledger.GetAsync(user, id, ct) is not { } item)
            return new AgentReply("זה כבר לא רלוונטי.");

        if (parts[0] == "n")
        {
            await ledger.SetStateAsync(item, CoachState.Declined, ct: ct);
            return new AgentReply("בסדר.");
        }

        var tone = string.Join("\n", (await preferences.ListAsync(user, ct)).Select(p => "- " + p.Instruction));
        var system = "Write one short, warm Hebrew message (2 to 3 lines) from a small handmade jewelry business to a customer who bought about a year ago. " +
                     "Use the placeholder {שם} for her name. Thank her, say the piece is still special, and invite her to come back, without pressure or a discount unless asked. Answer with the message only.";
        var turn = await model.CompleteAsync(new ModelRequest(system, tone.Length > 0 ? "Her style preferences:\n" + tone : "", [],
            [ChatMessage.User("Customers: " + item.Title)]), ct);
        await usage.RecordAsync(user, model.ModelName, turn.Usage, ct);

        await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
        return new AgentReply(string.IsNullOrWhiteSpace(turn.Text) ? "לא הצלחתי לנסח עכשיו." : turn.Text.Trim() + "\n\n(את שולחת אותה בעצמך; החלפי {שם} בשם הלקוחה.)");
    }
}

/// <summary>
/// The first week: a few short questions (tone, emojis, rest days, what help she wants), one at a time, skippable,
/// asked after her first message was answered. The answers become ordinary saved preferences and switches.
/// </summary>
public class IntroService(AppDbContext db, PreferenceService preferences)
{
    public const string Prefix = "i:";
    public const int Steps = 4;
    public const int Skipped = 99;

    public static AgentReply Question(AgentUser user, int step) => step switch
    {
        0 => new AgentReply("כדי שאהיה שימושית באמת, כמה שאלות קצרות. איך תעדיפי שאכתוב לך?",
            Buttons: [new($"{Prefix}0:short", "קצר ועניני"), new($"{Prefix}0:warm", "חם ואישי"), new($"{Prefix}skip:x", "דלגי על השאלות")]),
        1 => new AgentReply("אימוג'ים בהודעות?",
            Buttons: [new($"{Prefix}1:yes", "כן, קצת"), new($"{Prefix}1:no", "בלי")]),
        2 => new AgentReply("יש ימי מנוחה שבהם אני לא כותבת לך מיוזמתי?",
            Buttons: [new($"{Prefix}2:none", "אין"), new($"{Prefix}2:saturday", "שבת"), new($"{Prefix}2:friday_saturday", "שישי אחה\"צ ושבת")]),
        _ => new AgentReply("ובאילו הודעות את רוצה שאפתח?",
            Buttons: [new($"{Prefix}3:all", "הכול"), new($"{Prefix}3:alerts", "רק התראות"), new($"{Prefix}3:none", "בינתיים לא")]),
    };

    public async Task<AgentReply?> HandleButtonAsync(AgentUser user, string buttonId, CancellationToken ct)
    {
        if (!buttonId.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = buttonId[Prefix.Length..].Split(':', 2);
        if (parts.Length != 2) return new AgentReply("לא הבנתי את הלחיצה.");

        if (parts[0] == "skip")
        {
            await SetStepAsync(user, Skipped, ct);
            return new AgentReply("בסדר, אפשר תמיד לשנות: פשוט תגידי לי איך תעדיפי.");
        }

        if (!int.TryParse(parts[0], out var step) || step is < 0 or >= Steps) return new AgentReply("לא הבנתי את הלחיצה.");

        switch (step, parts[1])
        {
            case (0, "short"): await preferences.SaveAsync(user, "tone", "תשובות קצרות ועניניות", false, ct); break;
            case (0, "warm"): await preferences.SaveAsync(user, "tone", "טון חם ואישי", false, ct); break;
            case (1, "yes"): await preferences.SaveAsync(user, "emoji", "אפשר אימוג'ים בודדים", false, ct); break;
            case (1, "no"): await preferences.SaveAsync(user, "emoji", "בלי אימוג'ים", false, ct); break;
            case (2, var days) when days is "none" or "saturday" or "friday_saturday":
                await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.RestDays, days), ct);
                break;
            case (3, "all"):
                await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.MorningSummary, true).SetProperty(u => u.WeeklyReview, true).SetProperty(u => u.CoachEnabled, true)
                    .SetProperty(u => u.StoreOrderAlerts, true).SetProperty(u => u.StuckOrderAlerts, true), ct);
                break;
            case (3, "alerts"):
                await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.StoreOrderAlerts, true).SetProperty(u => u.StuckOrderAlerts, true), ct);
                break;
        }

        // IntroStep counts the questions asked so far: asking question n makes it n + 1, the last answer makes it Steps + 1.
        var next = step + 1;
        await SetStepAsync(user, next >= Steps ? Steps + 1 : next + 1, ct);
        return next >= Steps
            ? new AgentReply("תודה, זה הכול. אפשר לשנות כל דבר בכל רגע, ולשאול אותי \"מה את יודעת עליי?\".")
            : Question(user, next);
    }

    public Task SetStepAsync(AgentUser user, int step, CancellationToken ct) =>
        db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IntroStep, step), ct);
}
