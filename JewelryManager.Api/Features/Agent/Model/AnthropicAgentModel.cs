using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Model;

/// <summary>The Claude implementation of <see cref="IAgentModel"/>. Nothing outside this file knows the SDK.</summary>
public class AnthropicAgentModel(IOptions<AgentOptions> options) : IAgentModel
{
    private readonly ModelOptions _config = options.Value.Model;
    private AnthropicClient? _client;

    public string ModelName => _config.Name;

    public async Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        _client ??= string.IsNullOrWhiteSpace(_config.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = _config.ApiKey };

        // The first block never changes between requests, so it is cached; the personal block comes after the
        // cache breakpoint and may differ per person without breaking the cache.
        var system = new List<TextBlockParam>
        {
            new() { Text = request.StaticSystem, CacheControl = new CacheControlEphemeral() },
        };
        if (!string.IsNullOrWhiteSpace(request.PersonalSystem))
            system.Add(new TextBlockParam { Text = request.PersonalSystem });

        var parameters = new MessageCreateParams
        {
            Model = _config.Name,
            MaxTokens = _config.MaxOutputTokens,
            System = system,
            Tools = request.Tools.Count == 0 ? null : request.Tools.Select(ToTool).Select(t => (ToolUnion)t).ToList(),
            Messages = request.Messages.Select(ToParam).ToList(),
            OutputConfig = new OutputConfig { Effort = ParseEffort(_config.Effort) },
        };

        var response = await _client.Messages.Create(parameters, ct);

        var blocks = new List<ChatBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text)) blocks.Add(new TextPart(text.Text));
            else if (block.TryPickToolUse(out ToolUseBlock? use)) blocks.Add(new ToolCallPart(use.ID, use.Name, JsonSerializer.Serialize(use.Input)));
            else if (block.TryPickThinking(out ThinkingBlock? thinking)) blocks.Add(new ReasoningPart(thinking.Thinking, thinking.Signature));
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted)) blocks.Add(new RedactedReasoningPart(redacted.Data));
        }

        var usage = new ModelUsage(
            (int)response.Usage.InputTokens,
            (int)response.Usage.OutputTokens,
            (int)(response.Usage.CacheReadInputTokens ?? 0),
            (int)(response.Usage.CacheCreationInputTokens ?? 0));

        var stop = response.StopReason?.ToString() ?? "";
        return new ModelTurn(blocks, stop.Contains("refusal", StringComparison.OrdinalIgnoreCase),
            stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) || stop.Contains("MaxTokens", StringComparison.OrdinalIgnoreCase), usage);
    }

    private static Tool ToTool(AgentToolSpec spec)
    {
        var properties = new Dictionary<string, JsonElement>();
        var required = new List<string>();

        if (spec.InputSchema.ValueKind == JsonValueKind.Object)
        {
            if (spec.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.Clone();

            if (spec.InputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                required.AddRange(req.EnumerateArray().Select(r => r.GetString()!).Where(r => r is not null));
        }

        return new Tool
        {
            Name = spec.Name,
            Description = spec.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static MessageParam ToParam(ChatMessage message)
    {
        var content = new List<ContentBlockParam>();
        foreach (var block in message.Blocks)
        {
            switch (block)
            {
                case TextPart t when !string.IsNullOrEmpty(t.Text):
                    content.Add(new TextBlockParam { Text = t.Text });
                    break;
                case ToolCallPart c:
                    content.Add(new ToolUseBlockParam
                    {
                        ID = c.Id,
                        Name = c.Name,
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.ArgsJson) ?? [],
                    });
                    break;
                case ToolResultPart r:
                    content.Add(new ToolResultBlockParam { ToolUseID = r.CallId, Content = r.Content, IsError = r.IsError });
                    break;
                case ReasoningPart r:
                    content.Add(new ThinkingBlockParam { Thinking = r.Thinking, Signature = r.Signature });
                    break;
                case RedactedReasoningPart r:
                    content.Add(new RedactedThinkingBlockParam { Data = r.Data });
                    break;
            }
        }

        return message.Role == "assistant"
            ? new AssistantMessageParam { Content = content }
            : new UserMessageParam { Content = content };
    }

    private static Effort ParseEffort(string value) => value.Trim().ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Low,
    };
}
