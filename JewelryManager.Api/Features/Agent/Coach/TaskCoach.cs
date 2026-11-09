using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Mcp;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Walks her open tasks through the ladder (reminder, offer to break down, one plain question, rest in "בצד")
/// and answers the buttons under those messages. At most one message per round, and every step is remembered
/// in the ledger so a finished or refused task is never raised again.
/// </summary>
public class TaskCoach(
    CoachLedger ledger,
    ProactiveOutbox outbox,
    IBackendApiFactory apis,
    BreakdownService breakdown,
    AuditService audit,
    ILogger<TaskCoach> log)
{
    public const string Prefix = "c:";

    public async Task RunAsync(AgentUser user, CancellationToken ct)
    {
        var api = apis.CreateFor(user.Id);
        var tasks = TaskView.ParseList(await api.GetAsync("/tasks", ct)).Where(t => !t.IsCompleted).ToList();
        if (tasks.Count == 0) return;

        var keys = tasks.Select(t => CoachLedger.TaskKey(t.Id)).Concat(tasks.Where(t => t.ParentTaskId != null).Select(t => CoachLedger.TaskKey(t.ParentTaskId!.Value)));
        var items = await ledger.ByKeysAsync(user, keys, ct);

        var now = DateTime.UtcNow;
        var today = AgentClock.LocalToday();
        var plan = new List<(TaskView Task, CoachItem? Item, LadderStep Step)>();
        foreach (var task in tasks)
        {
            items.TryGetValue(CoachLedger.TaskKey(task.Id), out var item);
            DateTime? parentMention = task.ParentTaskId is { } p && items.TryGetValue(CoachLedger.TaskKey(p), out var parentItem) ? parentItem.LastMentionedAt : null;
            var step = TaskLadder.Decide(task, item, now, today, parentMention);
            if (step != LadderStep.None) plan.Add((task, item, step));
        }

        // Resting tasks need no message: they just leave the active ladder.
        foreach (var (task, item, _) in plan.Where(x => x.Step == LadderStep.Park))
            await ParkAsync(user, api, task, item, ct);

        // Without the coach switched on, only the reminders she asked for (a due date) are sent.
        var talk = plan.Where(x => x.Step is LadderStep.RemindDue or LadderStep.RemindStale or LadderStep.OfferBreakdown or LadderStep.AskDecision)
            .Where(x => user.CoachEnabled || x.Step == LadderStep.RemindDue)
            .OrderBy(x => x.Step == LadderStep.RemindDue ? 0 : 1).ThenBy(x => x.Task.UpdatedAt);

        foreach (var (task, _, step) in talk)
        {
            if (await SpeakAsync(user, task, step, ct)) return;
        }
    }

    private async Task<bool> SpeakAsync(AgentUser user, TaskView task, LadderStep step, CancellationToken ct)
    {
        var item = await ledger.EnsureAsync(user, CoachLedger.TaskKey(task.Id), CoachKind.Task, task.Title, task.Id, ct);
        var days = Math.Max(1, (int)(DateTime.UtcNow - task.UpdatedAt).TotalDays);

        var (text, buttons, stage, cls) = step switch
        {
            LadderStep.RemindDue => (TaskCoachMessages.Due(task.Title, task.DueDate), Buttons(item.Id, ("done", "עשיתי"), ("post", "דחי בשבוע"), ("snooze", "לא עכשיו")), 1, MessageClass.Scheduled),
            LadderStep.RemindStale => (TaskCoachMessages.Stale(task.Title, days), Buttons(item.Id, ("done", "עשיתי"), ("post", "דחי בשבוע"), ("snooze", "לא עכשיו")), 1, MessageClass.Coaching),
            LadderStep.OfferBreakdown => (TaskCoachMessages.Offer(task.Title), Buttons(item.Id, ("bdy", "כן, לפרק"), ("bdn", "לא, תודה"), ("snooze", "אחר כך")), 2, MessageClass.Coaching),
            _ => (TaskCoachMessages.Decide(task.Title), Buttons(item.Id, ("dmo", "לדחות לחודש"), ("dpk", "להוריד מהרשימה"), ("dkp", "להשאיר")), 3, MessageClass.Coaching),
        };

        if (!await outbox.SendAsync(user, text, cls, CoachKind.Task, buttons, ct)) return false;

        item.Stage = stage;
        await ledger.MentionedAsync(item, ct);
        return true;
    }

    private static List<ReplyButton> Buttons(Guid itemId, params (string Code, string Title)[] buttons) =>
        buttons.Select(b => new ReplyButton($"{Prefix}{b.Code}:{itemId}", b.Title)).ToList();

    // ── Her answers ───────────────────────────────────────────────────────────

    public async Task<AgentReply?> HandleButtonAsync(AgentUser user, string buttonId, CancellationToken ct)
    {
        if (!buttonId.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = buttonId[Prefix.Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var itemId)) return new AgentReply("לא הבנתי את הלחיצה.");

        var item = await ledger.GetAsync(user, itemId, ct);
        if (item?.TaskId is not { } taskId) return new AgentReply("המשימה הזו כבר לא ברשימה.");

        var api = apis.CreateFor(user.Id);
        try
        {
            switch (parts[0])
            {
                case "done":
                    return await CompleteAsync(user, api, item, taskId, ct);

                case "post":
                    await MoveDueAsync(api, taskId, AgentClock.LocalToday().AddDays(7), ct);
                    await ledger.SetStateAsync(item, CoachState.Suggested, stage: 0, ct: ct);
                    return new AgentReply("דחיתי לשבוע הבא.");

                case "snooze":
                    await api.SendAsync(HttpMethod.Patch, $"/tasks/{taskId}/coach", new { action = "snooze", until = DateTime.UtcNow.AddDays(7) }, ct);
                    return new AgentReply("בסדר, אחזור לזה בעוד שבוע.");

                case "bdy":
                    await ledger.SetStateAsync(item, CoachState.Accepted, stage: 2, ct: ct);
                    return await breakdown.ProposeAsync(user, item, taskId, ct);

                case "bdn":
                    // Declined the breakdown: ask plainly what to do instead, right away.
                    item.Stage = 3;
                    await ledger.MentionedAsync(item, ct);
                    return new AgentReply(TaskCoachMessages.Decide(item.Title), null, Buttons(item.Id, ("dmo", "לדחות לחודש"), ("dpk", "להוריד מהרשימה"), ("dkp", "להשאיר")));

                case "dmo":
                    await MoveDueAsync(api, taskId, AgentClock.LocalToday().AddDays(30), ct);
                    await api.SendAsync(HttpMethod.Patch, $"/tasks/{taskId}/coach", new { action = "snooze", until = DateTime.UtcNow.AddDays(30) }, ct);
                    item.MentionCount = 0;
                    await ledger.SetStateAsync(item, CoachState.Suggested, stage: 0, ct: ct);
                    return new AgentReply("דחיתי לעוד חודש. אני לא אזכיר אותה עד אז.");

                case "dpk":
                    await ParkAsync(user, api, null, item, ct, taskId);
                    return new AgentReply("הוצאתי אותה בצד. היא לא תוזכר שוב, ואפשר תמיד לבקש \"מה בצד?\".");

                case "dkp":
                    await api.SendAsync(HttpMethod.Patch, $"/tasks/{taskId}/coach", new { action = "snooze", until = DateTime.UtcNow.AddDays(14) }, ct);
                    item.MentionCount = 0;
                    await ledger.SetStateAsync(item, CoachState.Suggested, stage: 0, ct: ct);
                    return new AgentReply("משאירה אותה. אשאל שוב רק בעוד שבועיים.");

                default:
                    return new AgentReply("לא הבנתי את הלחיצה.");
            }
        }
        catch (BackendApiException ex)
        {
            log.LogWarning(ex, "Task coach action {Action} failed", parts[0]);
            return new AgentReply($"לא הצלחתי: {ex.Message}");
        }
    }

    private async Task<AgentReply> CompleteAsync(AgentUser user, IBackendApi api, CoachItem item, Guid taskId, CancellationToken ct)
    {
        var before = await api.GetAsync($"/tasks/{taskId}", ct);
        var previous = before.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "NEW";

        await api.SendAsync(HttpMethod.Patch, $"/tasks/{taskId}/status", new { status = "COMPLETED" }, ct);
        await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
        await audit.RecordAsync(user, "complete_task", JsonSerializer.Serialize(new { taskId }), true, "ok",
            new UndoCall("PATCH", $"/tasks/{taskId}/status", new { status = previous }), ct);

        return new AgentReply($"כל הכבוד. סגרתי: {item.Title}. לביטול: {AgentProcessor.UndoWord(user)}");
    }

    private async Task ParkAsync(AgentUser user, IBackendApi api, TaskView? task, CoachItem? item, CancellationToken ct, Guid? taskId = null)
    {
        var id = task?.Id ?? taskId ?? throw new ArgumentException("A task is required");
        await api.SendAsync(HttpMethod.Patch, $"/tasks/{id}/coach", new { action = "park" }, ct);
        if (item is not null) await ledger.SetStateAsync(item, CoachState.Parked, stage: 4, ct: ct);
    }

    private static async Task MoveDueAsync(IBackendApi api, Guid taskId, DateOnly date, CancellationToken ct)
    {
        var current = await api.GetAsync($"/tasks/{taskId}", ct);
        string? Text(string name) => current.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var body = new
        {
            title = Text("title"),
            content = Text("content"),
            status = Text("status") ?? "NEW",
            orderId = Text("orderId"),
            category = Text("category"),
            dueDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        await api.SendAsync(HttpMethod.Put, $"/tasks/{taskId}", body, ct);
    }
}

/// <summary>What the coach says about a task: short, no blame, never more than one question.</summary>
public static class TaskCoachMessages
{
    public static string Due(string title, DateOnly? due) =>
        due is { } d && d < AgentClock.LocalToday()
            ? $"המשימה \"{title}\" הייתה אמורה להיות מוכנה ב-{d:dd/MM}."
            : $"היום מועד היעד של המשימה: {title}.";

    public static string Stale(string title, int days) =>
        $"המשימה \"{title}\" פתוחה כבר {days} ימים. מה עושים איתה?";

    public static string Offer(string title) =>
        $"נראה ש\"{title}\" נתקעה. אולי היא פשוט גדולה. לפרק אותה לצעדים קטנים?";

    public static string Decide(string title) =>
        $"\"{title}\" עדיין פתוחה. מה עושים איתה?";
}
