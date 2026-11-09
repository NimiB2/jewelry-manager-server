using System.Text.Json;
using System.Text.Json.Nodes;

namespace JewelryManager.Mcp;

/// <summary>
/// Cuts an API answer down to what the model may see and can afford: only allowed fields, no nulls,
/// short lists, short text, and a hard size cap. A field that is not on the allow-list never leaves the
/// system, so a field added to the API later stays hidden until someone opts it in.
/// </summary>
public static class ResultShaper
{
    public const int MaxChars = 2000;
    public const int MaxText = 160;
    private static readonly int[] ListSteps = [20, 10, 5, 3, 1];

    public static JsonNode? Shape(JsonElement source, IReadOnlySet<string> allowedKeys, int maxItems = 20)
    {
        if (source.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

        var original = JsonNode.Parse(source.GetRawText());
        JsonNode? shaped = null;

        foreach (var step in ListSteps.Where(s => s <= maxItems).DefaultIfEmpty(1))
        {
            shaped = Clean(original, allowedKeys, step);
            if (shaped is null || shaped.ToJsonString(ToolJson.Options).Length <= MaxChars) return shaped;
        }

        // Still too long even with one item per list: keep the start and say it was cut.
        var text = shaped!.ToJsonString(ToolJson.Options);
        return new JsonObject { ["truncated"] = text[..MaxChars] };
    }

    private static JsonNode? Clean(JsonNode? node, IReadOnlySet<string> allowed, int maxItems)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (!allowed.Contains(key)) continue;
                    var cleaned = Clean(value, allowed, maxItems);
                    if (cleaned is not null) result[key] = cleaned;
                }

                return result.Count == 0 ? null : result;

            case JsonArray array:
                var items = array.Take(maxItems).Select(i => Clean(i, allowed, maxItems)).Where(i => i is not null).ToList();
                var list = new JsonArray();
                foreach (var item in items) list.Add(item!.DeepClone());
                if (array.Count > maxItems) list.Add($"... {array.Count - maxItems} more");
                return list;

            case JsonValue value:
                if (value.TryGetValue<string>(out var text))
                    return JsonValue.Create(text.Length > MaxText ? text[..MaxText] + "..." : text);
                return value.DeepClone();

            default:
                return null;
        }
    }

    public static IReadOnlySet<string> Keys(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
}
