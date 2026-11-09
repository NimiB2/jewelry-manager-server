using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Mcp;

namespace JewelryManager.Api.Features.Agent;

/// <summary>Runs one business tool and writes the audit entry, whatever the outcome.</summary>
public class ToolExecutor(AuditService audit, ILogger<ToolExecutor> log)
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
        return outcome;
    }
}

/// <summary>
/// The agent's own tools: remembered preferences and nicknames, undo, and its notification switches.
/// They touch only the agent's tables (and, for undo, the same API calls the tools themselves used).
/// </summary>
public class LocalToolRunner(
    PreferenceService preferences,
    AliasService aliases,
    AuditService audit,
    AgentUserDirectory directory,
    IBackendApiFactory apis,
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

            default:
                return new ToolOutcome(false, "unknown tool", null);
        }
    }

    /// <summary>Takes back the newest recent change. The entry is claimed first, so a double request undoes once.</summary>
    public async Task<ToolOutcome> UndoAsync(AgentUser user, CancellationToken ct)
    {
        var entry = await audit.LastUndoableAsync(user, ct);
        if (entry?.UndoJson is null) return new ToolOutcome(false, "there is nothing recent to undo", null);
        if (!await audit.MarkUndoneAsync(user, entry.Id, ct)) return new ToolOutcome(false, "there is nothing recent to undo", null);

        try
        {
            var undo = JsonSerializer.Deserialize<UndoCall>(entry.UndoJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await apis.CreateFor(user.Id).SendAsync(new HttpMethod(undo.Method), undo.Path, undo.Body, ct);
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
