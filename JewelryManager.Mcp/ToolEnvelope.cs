using System.Text.Json;
using System.Text.Json.Nodes;

namespace JewelryManager.Mcp;

/// <summary>A call that can be sent to the API later, e.g. to take back what a tool just did.</summary>
public record UndoCall(string Method, string Path, object? Body);

/// <summary>
/// What every tool returns, as one JSON string: the short result for the model, an optional undo
/// recipe for the agent's own bookkeeping (the model never sees it), or an error line.
/// </summary>
public static class ToolEnvelope
{
    public static string Ok(JsonNode? result, UndoCall? undo = null)
    {
        var node = new JsonObject { ["ok"] = true, ["result"] = result };
        if (undo is not null)
            node["undo"] = JsonSerializer.SerializeToNode(undo, ToolJson.Options);
        return node.ToJsonString(ToolJson.Options);
    }

    public static string Error(string message) =>
        new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(ToolJson.Options);
}

/// <summary>The parsed form of an envelope, used by the agent.</summary>
public record ToolOutcome(bool Ok, string ModelText, UndoCall? Undo)
{
    public static ToolOutcome Parse(string envelope)
    {
        try
        {
            var node = JsonNode.Parse(envelope)?.AsObject();
            if (node is null) return new ToolOutcome(false, "Tool returned nothing", null);

            if (node["ok"]?.GetValue<bool>() != true)
                return new ToolOutcome(false, node["error"]?.GetValue<string>() ?? "The action failed", null);

            UndoCall? undo = null;
            if (node["undo"] is JsonNode undoNode)
                undo = undoNode.Deserialize<UndoCall>(ToolJson.Options);

            return new ToolOutcome(true, node["result"]?.ToJsonString(ToolJson.Options) ?? "done", undo);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not our envelope: pass the raw text on, capped, so the model still gets something.
            return new ToolOutcome(true, envelope.Length > 2000 ? envelope[..2000] : envelope, null);
        }
    }
}
