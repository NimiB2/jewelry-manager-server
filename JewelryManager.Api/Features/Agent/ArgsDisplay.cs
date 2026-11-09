using System.Text.Json;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Turns the real arguments of a pending change into a short Hebrew line. It is shown under the model's own
/// summary, so the person sees what will actually run even if the summary is wrong or was bent by hostile text.
/// </summary>
public static class ArgsDisplay
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orderNumber"] = "הזמנה",
        ["itemNumber"] = "פריט",
        ["productName"] = "מוצר",
        ["quantity"] = "כמות",
        ["items"] = "פריטים",
        ["customer"] = "לקוחה",
        ["notes"] = "הערות",
        ["status"] = "סטטוס",
        ["stage"] = "שלב",
        ["discountPercent"] = "הנחה %",
        ["discountFinalAmount"] = "סכום סופי",
        ["discountReason"] = "סיבת הנחה",
        ["amount"] = "סכום",
        ["supplier"] = "ספק",
        ["description"] = "תיאור",
        ["date"] = "תאריך",
        ["typeName"] = "סוג",
        ["category"] = "קטגוריה",
        ["note"] = "הערה",
    };

    public static string Render(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";

            var parts = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("summary") || p.Value.ValueKind == JsonValueKind.Null) continue;
                var label = Labels.TryGetValue(p.Name, out var l) ? l : p.Name;
                parts.Add($"{label}: {Value(p.Value)}");
            }

            return string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Value(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Value)),
            JsonValueKind.Object => string.Join(" ", value.EnumerateObject().Select(o => Value(o.Value))),
            _ => value.ToString(),
        };
        return text.Length > 80 ? text[..80] + "..." : text;
    }
}
