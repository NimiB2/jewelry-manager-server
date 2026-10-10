using System.Globalization;
using System.Text.Json;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>One line of an order as Shopify sent it, before any matching to the catalog.</summary>
/// <summary>ExternalProductId is empty for a custom item that the store does not keep as a product.</summary>
public record IncomingLine(
    string ExternalLineId, string Title, string? VariantTitle, string? Sku, int Quantity, decimal UnitPrice, string? ExternalProductId = null);

/// <summary>An order as Shopify sent it, reduced to what this system uses. All text is already trimmed and bounded.</summary>
public record IncomingOrder(
    string ExternalId,
    string DisplayName,
    DateOnly Date,
    string? Customer,
    decimal Total,
    decimal TotalDiscounts,
    string? Currency,
    string? Note,
    IReadOnlyList<IncomingLine> Lines);

/// <summary>Reads the orders/create webhook body. Anything that is not a usable order gives null.</summary>
public static class ShopifyOrderParser
{
    private const int MaxLines = 100;
    private const decimal MaxMoney = 100_000_000m;

    public static IncomingOrder? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var id = Text(root, "id", 40);
            if (id is null || !root.TryGetProperty("line_items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            if (items.GetArrayLength() is 0 or > MaxLines) return null;

            var lines = new List<IncomingLine>();
            foreach (var item in items.EnumerateArray())
            {
                var lineId = Text(item, "id", 40);
                var title = Text(item, "title", 300) ?? Text(item, "name", 300);
                var quantity = Whole(item, "quantity");
                var price = Money(item, "price");
                if (lineId is null || title is null || quantity is not (> 0 and <= 1_000) || price is null) return null;

                lines.Add(new IncomingLine(
                    lineId, title, Text(item, "variant_title", 200), Text(item, "sku", 100), quantity.Value, price.Value,
                    Text(item, "product_id", 40)));
            }

            var total = Money(root, "total_price");
            if (total is null) return null;

            return new IncomingOrder(
                id,
                Text(root, "name", 50) ?? $"#{id}",
                OrderDate(root),
                CustomerName(root),
                total.Value,
                Money(root, "total_discounts") ?? 0m,
                Text(root, "currency", 10),
                Text(root, "note", 2_000),
                lines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Shopify sends ids as numbers and money as strings ("150.00"); both are read as text and checked.
    private static string? Text(JsonElement element, string name, int max)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > max ? text[..max] : text;
    }

    private static int? Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    private static decimal? Money(JsonElement element, string name)
    {
        var text = Text(element, name, 30);
        if (text is null || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) return null;
        return amount is >= 0 and < MaxMoney ? amount : null;
    }

    private static DateOnly OrderDate(JsonElement root)
    {
        // The shop's own calendar day, as written in the timestamp (not converted to UTC).
        var text = Text(root, "created_at", 40);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
            ? DateOnly.FromDateTime(created.DateTime)
            : DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static string? CustomerName(JsonElement root)
    {
        if (root.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object)
        {
            var name = Join(Text(customer, "first_name", 100), Text(customer, "last_name", 100));
            if (name is not null) return name;
        }

        foreach (var address in new[] { "shipping_address", "billing_address" })
            if (root.TryGetProperty(address, out var a) && a.ValueKind == JsonValueKind.Object && Text(a, "name", 200) is { } named)
                return named;

        return null;
    }

    private static string? Join(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return name.Length == 0 ? null : name;
    }
}
