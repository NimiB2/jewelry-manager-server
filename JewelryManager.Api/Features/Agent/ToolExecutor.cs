using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Coach;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Mcp;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent;

/// <summary>Runs one business tool and writes the audit entry, whatever the outcome.</summary>
public partial class ToolExecutor(AuditService audit, CoachLedger ledger, ILogger<ToolExecutor> log)
{
    public async Task<ToolOutcome> RunAsync(IToolSession session, AgentUser user, string name, string argsJson, CancellationToken ct)
    {
        string envelope;
        try
        {
            envelope = await session.CallAsync(name, argsJson, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Tool {Tool} failed", name);
            envelope = ToolEnvelope.Error("The action failed");
        }

        var outcome = ToolOutcome.Parse(envelope);
        await audit.RecordAsync(user, name, argsJson, outcome.Ok, outcome.Ok ? "ok" : outcome.ModelText, outcome.Undo, ct);

        // A task she finished must never be raised again: the ledger closes in the same step.
        if (outcome.Ok && name == "complete_task" && TaskIdIn(outcome.Undo?.Path) is { } taskId)
            await ledger.TaskDoneAsync(user, taskId, ct);

        return outcome;
    }

    public static Guid? TaskIdIn(string? path)
    {
        var match = path is null ? null : TaskPath().Match(path);
        return match is { Success: true } && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    [GeneratedRegex("^/tasks/([0-9a-fA-F-]{36})/status$")]
    private static partial Regex TaskPath();
}

/// <summary>
/// The agent's own tools: remembered preferences and nicknames, undo, its notification switches and the coach's
/// settings. They touch only the agent's tables (and, for undo, the same API calls the tools themselves used).
/// </summary>
public class LocalToolRunner(
    AppDbContext db,
    PreferenceService preferences,
    AliasService aliases,
    AuditService audit,
    AgentUserDirectory directory,
    IBackendApiFactory apis,
    CoachLedger ledger,
    RhythmService rhythm,
    OccasionPlanner occasions,
    MetalIndexService metals,
    ILogger<LocalToolRunner> log)
{
    public async Task<ToolOutcome> RunAsync(AgentUser user, string name, string argsJson, CancellationToken ct)
    {
        ToolOutcome outcome;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            outcome = await DispatchAsync(user, name, doc.RootElement, ct);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            outcome = new ToolOutcome(false, ex.Message, null);
        }

        await audit.RecordAsync(user, name, argsJson, outcome.Ok, outcome.Ok ? "ok" : outcome.ModelText, null, ct);
        return outcome;
    }

    private async Task<ToolOutcome> DispatchAsync(AgentUser user, string name, JsonElement args, CancellationToken ct)
    {
        switch (name)
        {
            case LocalTools.SavePreference:
                var problem = await preferences.SaveAsync(user, Text(args, "key"), Text(args, "instruction"), Flag(args, "forEveryone"), ct);
                return problem is null ? Done("saved") : new ToolOutcome(false, problem, null);

            case LocalTools.ListPreferences:
                var all = await preferences.ListAsync(user, ct);
                return Done(all.Count == 0 ? "nothing is remembered" : string.Join("\n", all.Select(p => $"{p.Key}: {p.Instruction}")));

            case LocalTools.DeletePreference:
                return await preferences.DeleteAsync(user, Text(args, "key"), ct) ? Done("forgotten") : new ToolOutcome(false, "no such preference", null);

            case LocalTools.SaveAlias:
                var aliasProblem = await aliases.SaveAsync(user, Text(args, "nickname"), Text(args, "fullName"), ct);
                return aliasProblem is null ? Done("saved") : new ToolOutcome(false, aliasProblem, null);

            case LocalTools.DeleteAlias:
                return await aliases.DeleteAsync(user, Text(args, "nickname"), ct) ? Done("forgotten") : new ToolOutcome(false, "no such nickname", null);

            case LocalTools.SetNotifications:
                await directory.SetNotificationAsync(user.Id, Text(args, "kind"), Flag(args, "enabled"), ct);
                return Done("updated");

            case LocalTools.UndoLast:
                return await UndoAsync(user, ct);

            case LocalTools.MarkItemDone:
                return await MarkDoneAsync(user, Text(args, "title"), ct);

            case LocalTools.MuteTopic:
            case LocalTools.UnmuteTopic:
                var topic = Text(args, "topic");
                if (!CoachKind.Mutable.Contains(topic)) return new ToolOutcome(false, "topic must be one of: " + string.Join(", ", CoachKind.Mutable), null);
                if (name == LocalTools.MuteTopic) await ledger.MuteAsync(user, topic, ct); else await ledger.UnmuteAsync(user, topic, ct);
                return Done(name == LocalTools.MuteTopic ? "will not raise this again" : "back on");

            case LocalTools.SetOccasion:
                var occasionProblem = await occasions.SetAsync(user, Text(args, "code"), Flag(args, "enabled"), ct);
                return occasionProblem is null ? Done("updated") : new ToolOutcome(false, occasionProblem, null);

            case LocalTools.SetRestDays:
                var days = Text(args, "days");
                if (days is not ("none" or "saturday" or "friday_saturday")) return new ToolOutcome(false, "days must be none, saturday or friday_saturday", null);
                await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.RestDays, days), ct);
                return Done("saved");

            case LocalTools.AddObligation:
                var day = args.TryGetProperty("dayOfMonth", out var d) && d.TryGetInt32(out var n) ? n : 0;
                var obligationProblem = await rhythm.AddObligationAsync(user, Text(args, "title"), day, ct);
                return obligationProblem is null ? Done("a task will be created every month") : new ToolOutcome(false, obligationProblem, null);

            case LocalTools.ListObligations:
                var duties = await rhythm.ListObligationsAsync(user, ct);
                return Done(duties.Count == 0 ? "no monthly duties" : string.Join("\n", duties.Select(o => $"day {o.DayOfMonth}: {o.Title}")));

            case LocalTools.RemoveObligation:
                return await rhythm.RemoveObligationAsync(user, Text(args, "title"), ct) ? Done("stopped") : new ToolOutcome(false, "no such duty", null);

            case LocalTools.SetFocus:
                return await SetFocusAsync(user, Text(args, "area"), Text(args, "firstStep"), ct);

            case LocalTools.MapMaterial:
                var purity = args.TryGetProperty("purity", out var p) && p.TryGetDecimal(out var value) ? value : 0;
                var mapProblem = await metals.MapMaterialAsync(user, Text(args, "material"), Text(args, "metal"), purity, ct);
                return mapProblem is null ? Done("saved") : new ToolOutcome(false, mapProblem, null);

            case LocalTools.ListCoachState:
                return Done(await DescribeStateAsync(user, ct));

            default:
                return new ToolOutcome(false, "unknown tool", null);
        }
    }

    private async Task<ToolOutcome> MarkDoneAsync(AgentUser user, string title, CancellationToken ct)
    {
        var open = await ledger.OpenItemsAsync(user, 30, ct);
        var match = open.FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase))
                    ?? open.FirstOrDefault(i => CoachLedger.LooksLikeAny(title, [i.Title]) || i.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (match is null) return new ToolOutcome(false, "no open suggestion matches; if it is a task, use complete_task", null);

        var item = await ledger.GetAsync(user, match.Id, ct);
        if (item is null) return new ToolOutcome(false, "no open suggestion matches", null);
        await ledger.SetStateAsync(item, CoachState.Done, ct: ct);
        return Done($"closed: {item.Title}");
    }

    private async Task<ToolOutcome> SetFocusAsync(AgentUser user, string area, string firstStep, CancellationToken ct)
    {
        string[] areas = [FocusArea.Advertising, FocusArea.NewJewelry, FocusArea.Website, FocusArea.Customers, FocusArea.Admin, FocusArea.Occasion];
        if (!areas.Contains(area)) return new ToolOutcome(false, "area must be one of: " + string.Join(", ", areas), null);

        var step = string.IsNullOrWhiteSpace(firstStep) ? null : PreferenceRules.Clean(firstStep);
        var row = await db.CoachFocuses.FirstOrDefaultAsync(f => f.BusinessId == user.BusinessId, ct);
        if (row is null)
            db.CoachFocuses.Add(new CoachFocus { Id = Guid.NewGuid(), BusinessId = user.BusinessId, Area = area, Reason = "chosen by her", FirstStep = step, StartedAt = DateTime.UtcNow });
        else { row.Area = area; row.Reason = "chosen by her"; row.FirstStep = step; row.StartedAt = DateTime.UtcNow; }

        await db.SaveChangesAsync(ct);
        return Done("saved");
    }

    private async Task<string> DescribeStateAsync(AgentUser user, CancellationToken ct)
    {
        var text = new StringBuilder();
        var focus = await db.CoachFocuses.AsNoTracking().FirstOrDefaultAsync(f => f.BusinessId == user.BusinessId, ct);
        text.AppendLine(focus is null ? "focus: none" : $"focus: {FocusArea.Label(focus.Area)} ({focus.FirstStep})");

        var enabled = await db.CoachOccasions.AsNoTracking().Where(o => o.BusinessId == user.BusinessId && o.Enabled).Select(o => o.Code).ToListAsync(ct);
        text.AppendLine("occasions: " + (enabled.Count == 0 ? "not chosen yet" : string.Join(", ", enabled.Select(c => OccasionCalendar.Find(c)?.Name ?? c))));

        var duties = await rhythm.ListObligationsAsync(user, ct);
        text.AppendLine("monthly duties: " + (duties.Count == 0 ? "none" : string.Join("; ", duties.Select(o => $"{o.DayOfMonth}: {o.Title}"))));

        var muted = new List<string>();
        foreach (var kind in CoachKind.Mutable) if (await ledger.IsMutedAsync(user, kind, ct)) muted.Add(kind);
        text.AppendLine("muted: " + (muted.Count == 0 ? "none" : string.Join(", ", muted)));

        var open = await ledger.OpenItemsAsync(user, 8, ct);
        text.AppendLine("open suggestions: " + (open.Count == 0 ? "none" : string.Join("; ", open.Select(i => i.Title))));
        var fresh = await db.AgentUsers.AsNoTracking().FirstAsync(u => u.Id == user.Id, ct);
        text.AppendLine($"rest days: {fresh.RestDays}");
        return text.ToString().TrimEnd();
    }

    /// <summary>Takes back the newest recent change. The entry is claimed first, so a double request undoes once.</summary>
    public async Task<ToolOutcome> UndoAsync(AgentUser user, CancellationToken ct)
    {
        var entry = await audit.LastUndoableAsync(user, ct);
        if (entry?.UndoJson is null) return new ToolOutcome(false, "there is nothing recent to undo", null);
        if (!await audit.MarkUndoneAsync(user, entry.Id, ct)) return new ToolOutcome(false, "there is nothing recent to undo", null);

        try
        {
            var undo = JsonSerializer.Deserialize<UndoCall>(entry.UndoJson, ToolJson.Options)!;
            await apis.CreateFor(user.Id).SendAsync(new HttpMethod(undo.Method), undo.Path, undo.Body, ct);

            // Undoing "done" re-opens the task, so its reminders start over instead of staying closed.
            if (entry.Tool == "complete_task" && ToolExecutor.TaskIdIn(undo.Path) is { } taskId)
                await ledger.TaskReopenedAsync(user, taskId, ct);

            return Done($"undone: {entry.Tool}");
        }
        catch (Exception ex) when (ex is BackendApiException or HttpRequestException or JsonException)
        {
            log.LogWarning(ex, "Undo of {Tool} failed", entry.Tool);
            return new ToolOutcome(false, "the change could not be taken back", null);
        }
    }

    private static ToolOutcome Done(string text) => new(true, text, null);

    private static string Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Flag(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
