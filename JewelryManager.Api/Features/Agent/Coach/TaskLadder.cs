using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Mcp;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>A task as the coach sees it (read through the API, so the same tenant rules apply).</summary>
public record TaskView(
    Guid Id,
    string Title,
    bool IsCompleted,
    bool IsAutomatic,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateOnly? DueDate,
    Guid? ParentTaskId,
    DateTime? SnoozedUntil,
    int DeferCount,
    DateTime? ParkedAt)
{
    public static List<TaskView> ParseList(JsonElement array)
    {
        var result = new List<TaskView>();
        if (array.ValueKind != JsonValueKind.Array) return result;
        foreach (var t in array.EnumerateArray())
        {
            var view = Parse(t);
            if (view is not null) result.Add(view);
        }

        return result;
    }

    public static TaskView? Parse(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("id", out var id) || !id.TryGetGuid(out var guid)) return null;

        string? Str(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        DateTime? Date(string name) => DateTime.TryParse(Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        Guid? GuidOf(string name) => Guid.TryParse(Str(name), out var g) ? g : null;

        var created = Date("createdAt") ?? DateTime.UtcNow;
        return new TaskView(
            guid,
            Str("title") ?? "",
            Str("status") == "COMPLETED",
            t.TryGetProperty("isAutomatic", out var auto) && auto.ValueKind == JsonValueKind.True,
            created,
            Date("updatedAt") ?? created,
            DateOnly.TryParse(Str("dueDate"), CultureInfo.InvariantCulture, out var due) ? due : null,
            GuidOf("parentTaskId"),
            Date("snoozedUntil"),
            t.TryGetProperty("deferCount", out var defer) && defer.TryGetInt32(out var n) ? n : 0,
            Date("parkedAt"));
    }
}

public enum LadderStep
{
    None,
    RemindDue,
    RemindStale,
    OfferBreakdown,
    AskDecision,
    Park,
}

/// <summary>
/// A task can end in only three ways: done, made smaller, or set aside by her. This is the ladder that gets it
/// there without nagging: one reminder, then an offer to break it into small steps, then one plain question,
/// then it rests in "בצד". Anything she does to the task starts the ladder over.
/// </summary>
public static class TaskLadder
{
    public const int StaleDays = 7;
    public const int StepGapDays = 3;

    public static LadderStep Decide(TaskView task, CoachItem? item, DateTime utcNow, DateOnly today, DateTime? parentLastMentionedUtc = null)
    {
        if (task.IsAutomatic || task.IsCompleted || task.ParkedAt is not null) return LadderStep.None;
        if (task.SnoozedUntil is { } snoozed && snoozed > utcNow) return LadderStep.None;
        if (item is { State: CoachState.Done or CoachState.Declined or CoachState.Parked or CoachState.Expired }) return LadderStep.None;
        if (item is { State: CoachState.Snoozed, SnoozedUntil: { } itemSnooze } && itemSnooze > utcNow) return LadderStep.None;

        var stage = item?.Stage ?? 0;
        var lastMention = item?.LastMentionedAt;

        // Something she did to the task after the last mention starts the ladder again.
        if (lastMention is { } mentioned && task.UpdatedAt > mentioned) stage = 0;

        var age = utcNow - task.UpdatedAt;
        var sinceMention = lastMention is { } m ? utcNow - m : TimeSpan.MaxValue;
        var isStep = task.ParentTaskId is not null;

        switch (stage)
        {
            case 0:
                if (task.DueDate is { } due && due <= today) return LadderStep.RemindDue;
                if (isStep)
                    return age >= TimeSpan.FromDays(StaleDays) && ParentQuiet(parentLastMentionedUtc, utcNow) ? LadderStep.RemindStale : LadderStep.None;
                if (task.DeferCount >= 2) return LadderStep.OfferBreakdown;
                if (TaskHeuristics.LooksBig(task.Title) && age >= TimeSpan.FromDays(1)) return LadderStep.OfferBreakdown;
                return age >= TimeSpan.FromDays(StaleDays) ? LadderStep.RemindStale : LadderStep.None;

            case 1:
                if (sinceMention < TimeSpan.FromDays(StepGapDays)) return LadderStep.None;
                return isStep ? LadderStep.AskDecision : LadderStep.OfferBreakdown;

            case 2:
                return sinceMention >= TimeSpan.FromDays(StepGapDays) ? LadderStep.AskDecision : LadderStep.None;

            case 3:
                return sinceMention >= TimeSpan.FromDays(StepGapDays) ? LadderStep.Park : LadderStep.None;

            default:
                return LadderStep.None;
        }
    }

    // A small step does not start its own reminders in the week its big task was last talked about.
    private static bool ParentQuiet(DateTime? parentLastMentioned, DateTime utcNow) =>
        parentLastMentioned is null || utcNow - parentLastMentioned.Value >= TimeSpan.FromDays(StaleDays);
}
