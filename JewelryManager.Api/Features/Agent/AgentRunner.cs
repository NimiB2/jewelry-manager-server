using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent;

/// <summary>What goes back to the person. A pending id means "show approve / cancel buttons under the text".</summary>
public record AgentReply(string Text, Guid? PendingActionId = null);

/// <summary>
/// One conversation turn: loads who the person is and what the agent remembers, lets the model ask for tools
/// (a few at most), applies the tool policy, and returns the final text. The model decides what to ask for;
/// this class decides what actually runs.
/// </summary>
public class AgentRunner(
    IAgentModel model,
    IToolSessionFactory sessions,
    ConversationStore conversation,
    PendingActionService pending,
    PreferenceService preferences,
    AliasService aliases,
    UsageService usage,
    ToolExecutor tools,
    LocalToolRunner localTools,
    IOptions<AgentOptions> options,
    ILogger<AgentRunner> log)
{
    private const string Fallback = "לא הצלחתי להבין. אפשר לנסח אחרת?";

    public async Task<AgentReply> RunAsync(AgentUser user, string userText, CancellationToken ct)
    {
        var limits = options.Value.Limits;
        var text = userText.Length > limits.MaxInputChars ? userText[..limits.MaxInputChars] : userText;

        var history = await conversation.RecentAsync(user, ct);
        var personal = PersonalContext.Render(user, await preferences.ListAsync(user, ct), await aliases.ListAsync(user, ct));

        var messages = new List<ChatMessage>(history) { ChatMessage.User(text) };
        await using var session = await sessions.StartAsync(user.Id, ct);
        var specs = session.Tools.Concat(LocalTools.Specs).ToList();

        AgentReply? reply = null;
        var calls = 0;

        for (var step = 0; step <= limits.MaxToolCallsPerMessage && reply is null; step++)
        {
            var turn = await model.CompleteAsync(new ModelRequest(AgentPrompt.Static, personal, specs, messages), ct);
            await usage.RecordAsync(user, model.ModelName, turn.Usage, ct);

            if (turn.Refused)
            {
                reply = new AgentReply("את זה אני לא יכולה לעשות.");
                break;
            }

            var requested = turn.ToolCalls;
            if (requested.Count == 0)
            {
                reply = new AgentReply(turn.Text.Length > 0 ? turn.Text : Fallback);
                break;
            }

            var results = new List<ChatBlock>();
            foreach (var call in requested)
            {
                if (reply is not null || ++calls > limits.MaxToolCallsPerMessage)
                {
                    results.Add(new ToolResultPart(call.Id, reply is not null ? "waiting for approval of another change" : "too many steps", true));
                    continue;
                }

                var (result, proposal) = await HandleCallAsync(session, user, call, ct);
                results.Add(new ToolResultPart(call.Id, result.Text, !result.Ok));
                if (proposal is not null) reply = proposal;
            }

            messages.Add(new ChatMessage("assistant", turn.Blocks));
            messages.Add(new ChatMessage("user", results));
        }

        reply ??= new AgentReply("זה דורש יותר מדי צעדים. אפשר לפצל לבקשות קטנות יותר?");

        await conversation.AddAsync(user, "user", text, ct);
        await conversation.AddAsync(user, "assistant", reply.Text, ct);
        return reply;
    }

    private async Task<(ToolResultText Result, AgentReply? Proposal)> HandleCallAsync(
        IToolSession session, AgentUser user, ToolCallPart call, CancellationToken ct)
    {
        if (!ToolPolicy.IsKnown(call.Name))
            return (new ToolResultText(false, "this tool is not available"), null);

        switch (ToolPolicy.TierOf(call.Name))
        {
            case ToolTier.Local:
                var local = await localTools.RunAsync(user, call.Name, call.ArgsJson, ct);
                return (new ToolResultText(local.Ok, local.ModelText), null);

            case ToolTier.Confirm:
                // Nothing runs yet: the change is stored and the person decides with a button.
                var summary = ReadSummary(call.ArgsJson) ?? $"לבצע: {call.Name}?";
                var action = await pending.CreateAsync(user, call.Name, call.ArgsJson, summary, ct);
                log.LogInformation("Pending action {Id} for tool {Tool}", action.Id, call.Name);
                var details = ArgsDisplay.Render(call.ArgsJson);
                var text = details.Length > 0 ? $"{summary}\n({details})" : summary;
                return (new ToolResultText(true, "waiting for the person's approval"), new AgentReply(text, action.Id));

            default: // Read and Immediate both run at once
                var outcome = await tools.RunAsync(session, user, call.Name, call.ArgsJson, ct);
                return (new ToolResultText(outcome.Ok, outcome.ModelText), null);
        }
    }

    private static string? ReadSummary(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var value = s.GetString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private record ToolResultText(bool Ok, string Text);
}
