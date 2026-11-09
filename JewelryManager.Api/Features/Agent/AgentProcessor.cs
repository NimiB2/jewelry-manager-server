using System.Text.RegularExpressions;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Media;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Everything that happens to one incoming WhatsApp message, from "is this person allowed" to the reply.
/// Button presses and plain yes/no answers to a waiting proposal are handled here without the model.
/// </summary>
public partial class AgentProcessor(
    AppDbContext db,
    AgentUserDirectory directory,
    IWhatsAppClient whatsapp,
    ITranscriber transcriber,
    AgentRunner runner,
    PendingActionService pending,
    ToolExecutor tools,
    IToolSessionFactory sessions,
    LocalToolRunner local,
    ConversationStore conversation,
    UsageService usage,
    ProactiveLog proactiveLog,
    IInvoiceIntake invoices,
    Coach.CoachButtonRouter coach,
    Coach.IntroService intro,
    MessageRateLimiter limiter,
    CurrentUserAccessor tenant,
    IOptions<AgentOptions> options,
    ILogger<AgentProcessor> log)
{
    private static readonly HashSet<string> YesWords = ["כן", "אשר", "אשרי", "מאשר", "מאשרת", "אוקי", "אוקיי", "בסדר", "סבבה", "yes", "ok"];
    private static readonly HashSet<string> NoWords = ["לא", "בטל", "בטלי", "תבטל", "תבטלי", "no", "cancel"];
    private static readonly HashSet<string> UndoWords = ["בטל", "בטלי", "תבטל", "תבטלי", "תבטלי את זה", "תבטל את זה", "undo"];

    public async Task HandleAsync(IncomingMessage message, CancellationToken ct)
    {
        if (!await MarkProcessedAsync(message.MessageId, ct)) return;

        var user = await directory.FindActiveByPhoneAsync(message.From, ct);
        if (user is null)
        {
            // Not on the list: no answer at all. The number is not logged.
            log.LogInformation("Ignored a message from a number that is not on the list");
            return;
        }

        // The invoice reader and the file store read the business from this accessor.
        tenant.UseBusiness(user.BusinessId);

        var limits = options.Value.Limits;
        if (!limiter.TryAcquire(user.Phone, limits.MessagesPerMinute))
        {
            await SendAsync(user, new AgentReply("רגע, יותר מדי הודעות. נסי שוב בעוד דקה."), ct);
            return;
        }

        // She wrote: whatever the agent said to her on its own was seen, so the coach may speak again later.
        await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.LastUserMessageAt, DateTime.UtcNow).SetProperty(u => u.CoachUnanswered, 0), ct);

        try { await whatsapp.MarkReadAsync(message.MessageId, ct); }
        catch (HttpRequestException) { /* the read mark is cosmetic */ }

        AgentReply reply;
        try
        {
            reply = await RouteAsync(user, message, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Handling a message failed");
            reply = new AgentReply("משהו השתבש. אפשר לנסות שוב?");
        }

        await SendAsync(user, reply, ct);

        // The first week: a few short questions, one at a time, after her first message was answered.
        if (user.IntroStep == 0 && message.Kind is IncomingKind.Text or IncomingKind.Audio)
        {
            await intro.SetStepAsync(user, 1, ct);
            await SendAsync(user, Coach.IntroService.Question(user, 0), ct);
        }
    }

    private Task<AgentReply> RouteAsync(AgentUser user, IncomingMessage message, CancellationToken ct) => message.Kind switch
    {
        IncomingKind.ButtonReply => HandleButtonAsync(user, message.ButtonId, ct),
        IncomingKind.Text => HandleTextAsync(user, message.Text ?? "", viaVoice: false, ct),
        IncomingKind.Audio => HandleVoiceAsync(user, message, ct),
        IncomingKind.Image or IncomingKind.Document => invoices.ReadAsync(user, message, ct),
        _ => Task.FromResult(new AgentReply("אני מבינה הודעות טקסט, הודעות קוליות ותמונות של חשבוניות.")),
    };

    private async Task<AgentReply> HandleButtonAsync(AgentUser user, string? buttonId, CancellationToken ct)
    {
        if (buttonId is not null && await coach.HandleAsync(user, buttonId, ct) is { } coached) return coached;

        var parts = (buttonId ?? "").Split(':', 2);
        if (parts.Length == 2 && Guid.TryParse(parts[1], out var id) && parts[0] is "ok" or "no")
            return await DecideAsync(user, id, parts[0] == "ok", ct);

        return new AgentReply("לא הבנתי את הלחיצה. אפשר לכתוב לי מה לעשות.");
    }

    private async Task<AgentReply> HandleVoiceAsync(AgentUser user, IncomingMessage message, CancellationToken ct)
    {
        if (!transcriber.IsAvailable || message.MediaId is null)
            return new AgentReply("הודעות קוליות עדיין לא מחוברות אצלי. אפשר לכתוב לי?");

        var audio = await whatsapp.DownloadMediaAsync(message.MediaId, options.Value.Limits.MaxMediaBytes, ct);
        if (audio is null) return new AgentReply("ההקלטה ארוכה מדי. אפשר לקצר?");

        // The recording only lives in memory for this call; it is never written anywhere.
        var text = await transcriber.TranscribeAsync(audio.Content, audio.MimeType, ct);
        if (text is null) return new AgentReply("לא שמעתי טוב. אפשר לחזור על זה?");

        return await HandleTextAsync(user, text, viaVoice: true, ct);
    }

    private async Task<AgentReply> HandleTextAsync(AgentUser user, string text, bool viaVoice, CancellationToken ct)
    {
        var word = Normalize(text);
        var open = await pending.FindOpenAsync(user, ct);

        if (open is not null && YesWords.Contains(word)) return await DecideAsync(user, open.Id, true, ct);
        if (open is not null && NoWords.Contains(word)) return await DecideAsync(user, open.Id, false, ct);

        if (UndoWords.Contains(word))
        {
            var undone = await local.UndoAsync(user, ct);
            return new AgentReply(undone.Ok ? "בוטל." : "אין מה לבטל כרגע.");
        }

        var budget = await usage.CheckAsync(user, ct);
        if (budget.State == BudgetState.Exhausted)
        {
            await TellDevelopersAsync(user, budget.Message!, ct);
            return new AgentReply(budget.Message!);
        }

        var reply = await runner.RunAsync(user, viaVoice ? "[הודעה קולית] " + text : text, ct);

        if (budget.State == BudgetState.NearLimit &&
            await proactiveLog.TryClaimAsync(user.Id, "budget_warn", AgentClock.LocalToday().ToString("yyyy-MM-dd"), ct))
            reply = reply with { Text = reply.Text + "\n\n" + budget.Message };

        return reply;
    }

    private async Task<AgentReply> DecideAsync(AgentUser user, Guid pendingId, bool approve, CancellationToken ct)
    {
        if (!approve)
        {
            var cancelled = await pending.ClaimAsync(user, pendingId, "cancelled", ct);
            return new AgentReply(cancelled is null ? "אין מה לבטל." : "בוטל. לא שיניתי כלום.");
        }

        var action = await pending.ClaimAsync(user, pendingId, "approved", ct);
        if (action is null) return new AgentReply("הבקשה כבר טופלה או שפג תוקפה.");

        // The approved change runs straight from what was stored: the model is not asked again.
        await using var session = await sessions.StartAsync(user.Id, ct);
        var outcome = await tools.RunAsync(session, user, action.ToolName, action.ArgsJson, ct);

        if (!outcome.Ok)
        {
            var failure = $"לא בוצע: {outcome.ModelText}";
            await conversation.AddAsync(user, "assistant", failure, ct);
            return new AgentReply(failure);
        }

        var filed = false;
        if (action.ToolName == "add_expense" && ExpenseIdFrom(outcome.Undo?.Path) is { } expenseId)
            filed = await invoices.AttachAsync(action.Id, expenseId);

        var text = "בוצע." + (filed ? " החשבונית נשמרה עם ההוצאה." : "") + (outcome.Undo is null ? "" : $" לביטול: {UndoWord(user)}");
        await conversation.AddAsync(user, "assistant", $"בוצע: {action.Summary}", ct);
        return new AgentReply(text);
    }

    private async Task SendAsync(AgentUser user, AgentReply reply, CancellationToken ct)
    {
        if (reply.Buttons is { Count: > 0 })
            await whatsapp.SendButtonsAsync(user.Phone, reply.Text, reply.Buttons, ct);
        else if (reply.PendingActionId is { } id)
            await whatsapp.SendButtonsAsync(user.Phone, reply.Text,
                [new ReplyButton($"ok:{id}", "אשר"), new ReplyButton($"no:{id}", "בטל")], ct);
        else
            await whatsapp.SendTextAsync(user.Phone, reply.Text, ct);
    }

    private async Task TellDevelopersAsync(AgentUser who, string message, CancellationToken ct)
    {
        var day = AgentClock.LocalToday().ToString("yyyy-MM-dd");
        foreach (var developer in (await directory.ListActiveAsync(ct)).Where(u => u.IsDeveloper))
        {
            if (!await proactiveLog.TryClaimAsync(developer.Id, "budget_alert", $"{day}:{who.Id}", ct)) continue;
            try { await whatsapp.SendTextAsync(developer.Phone, "התראת תקציב: " + message, ct); }
            catch (HttpRequestException ex) { log.LogWarning(ex, "Could not alert the developer"); }
        }
    }

    // A WhatsApp message id is handled once: Meta retries, and a retry must not act twice.
    private async Task<bool> MarkProcessedAsync(string messageId, CancellationToken ct)
    {
        // One atomic insert: the first caller gets 1 row, any retry gets 0 (and nothing is tracked or thrown).
        var now = DateTime.UtcNow;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "AgentProcessedMessages" ("MessageId", "ReceivedAt") VALUES ({messageId}, {now}) ON CONFLICT DO NOTHING""", ct);
        return inserted == 1;
    }

    public static string Normalize(string text) => Punctuation().Replace(text.Trim().ToLowerInvariant(), "").Trim();

    public static string UndoWord(AgentUser user) => user.Gender == "male" ? "בטל" : "בטלי";

    private static Guid? ExpenseIdFrom(string? path)
    {
        var last = path?.Split('/').LastOrDefault();
        return Guid.TryParse(last, out var id) ? id : null;
    }

    [GeneratedRegex(@"[.,!?'""׳״\-]+")]
    private static partial Regex Punctuation();
}
