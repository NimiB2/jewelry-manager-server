using System.Globalization;
using System.Text.Json;

namespace JewelryManager.Api.Features.Invoices;

/// <summary>The instruction sent to every provider, and the reading of whatever text comes back.</summary>
public static class InvoiceAnswerParser
{
    public static string BuildPrompt(IReadOnlyList<string> expenseTypes)
    {
        var types = expenseTypes.Count == 0 ? "(none)" : string.Join(" | ", expenseTypes);
        return $$"""
            You read invoices and receipts of a small handmade-jewelry business in Israel.
            Return ONLY one JSON object, no explanations, with exactly these keys:
            {"amount": number|null, "date": "YYYY-MM-DD"|null, "supplier": string|null, "description": string|null, "type": string|null}
            Rules:
            - amount: the total actually paid, including VAT, as a plain number in the invoice currency (no symbol).
            - date: the invoice date. Israeli dates are day/month/year.
            - supplier: the business name that issued the invoice.
            - description: a short summary in Hebrew of WHAT was bought (e.g. "חוט כסף 925 ושרשראות"), at most 200 characters.
            - type: exactly one of these categories, or null if none fits: {{types}}
            - Use null for anything you cannot read with confidence. Never guess.
            """;
    }

    /// <summary>Turns the model's text into a suggestion; anything unreadable becomes an empty field or null.</summary>
    public static InvoiceSuggestion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Models sometimes wrap the JSON in a code fence or add a sentence around it.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;

            var suggestion = new InvoiceSuggestion(
                ReadAmount(root), ReadDate(root), ReadText(root, "supplier"), ReadText(root, "description"), ReadText(root, "type"));

            return suggestion == new InvoiceSuggestion(null, null, null, null, null) ? null : suggestion;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? ReadAmount(JsonElement root)
    {
        if (!root.TryGetProperty("amount", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out var n) ? n : null;
        if (value.ValueKind != JsonValueKind.String) return null;

        // "1,234.50 ₪" style strings: keep digits, dot and minus only.
        var digits = new string(value.GetString()!.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateOnly? ReadDate(JsonElement root)
    {
        var text = ReadText(root, "date");
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}
