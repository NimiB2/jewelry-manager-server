using System.Text.Json;

namespace JewelryManager.Api.Features.Agent.Model;

/// <summary>A tool as the model sees it.</summary>
public record AgentToolSpec(string Name, string Description, JsonElement InputSchema);

/// <summary>One block inside a chat message. Kept neutral so the model provider can be swapped or faked in tests.</summary>
public abstract record ChatBlock;

public record TextPart(string Text) : ChatBlock;

public record ToolCallPart(string Id, string Name, string ArgsJson) : ChatBlock;

public record ToolResultPart(string CallId, string Content, bool IsError) : ChatBlock;

// Reasoning blocks must travel back to the model untouched, or the next step of a tool loop is rejected.
public record ReasoningPart(string Thinking, string Signature) : ChatBlock;

public record RedactedReasoningPart(string Data) : ChatBlock;

public record ChatMessage(string Role, IReadOnlyList<ChatBlock> Blocks)
{
    public static ChatMessage User(string text) => new("user", [new TextPart(text)]);

    public static ChatMessage Assistant(string text) => new("assistant", [new TextPart(text)]);
}

public record ModelRequest(
    string StaticSystem,
    string PersonalSystem,
    IReadOnlyList<AgentToolSpec> Tools,
    IReadOnlyList<ChatMessage> Messages);

public record ModelUsage(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens);

public record ModelTurn(IReadOnlyList<ChatBlock> Blocks, bool Refused, bool Truncated, ModelUsage Usage)
{
    public string Text => string.Join("\n", Blocks.OfType<TextPart>().Select(b => b.Text)).Trim();

    public IReadOnlyList<ToolCallPart> ToolCalls => Blocks.OfType<ToolCallPart>().ToList();
}

/// <summary>One call to the language model. The only place that knows which provider is behind it.</summary>
public interface IAgentModel
{
    string ModelName { get; }

    Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken ct);
}
