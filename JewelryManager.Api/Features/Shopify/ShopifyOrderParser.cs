using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>One line of an order as Shopify sent it, before any matching to the catalog.</summary>
/// <summary>ExternalProductId is empty for a custom item that the store does not keep as a product.</summary>
public record IncomingLine(
    string ExternalLineId, string Title, string? VariantTitle, string? Sku, int Quantity, decimal UnitPrice, string? ExternalProductId = null);

/// <summary>
/// A discount the store applied: a coupon (Label is the code) or an automatic/manual one (Label is its name).
/// Lines are the titles of the items it was taken off.
/// </summary>
public record IncomingDiscount(string Label, bool IsCoupon, IReadOnlyList<string> Lines);

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
    IReadOnlyList<IncomingLine> Lines,
    // The store's payment state ("paid", "pending", "refunded"...), and the discounts that were applied.
    string? FinancialStatus = null,
    IReadOnlyList<IncomingDiscount>? Discounts = null,
    // The customer's phone, if the store has one (account, shipping or billing address, or the order itself).
    string? Phone = null)
{
    /// <summary>
    /// Why the price went down, in the owner's words: the coupon code or the sale's name, and which items
    /// it was on when it was not the whole order. Null when the store named no discount.
    /// </summary>
    public string? DiscountReason()
    {
        if (Discounts is not { Count: > 0 }) return null;

        var parts = Discounts.Select(d =>
        {
            var name = d.IsCoupon ? $"קופון {d.Label}" : d.Label;
            var partial = d.Lines.Count > 0 && d.Lines.Count < Lines.Count;
            return partial ? $"{name} (על: {string.Join(", ", d.Lines)})" : name;
        });

        var text = string.Join(" · ", parts);
        return text.Length > 300 ? text[..300] : text;
    }

    /// <summary>Only a fully paid order is of interest; the others wait for the store's "paid" event.</summary>
    public bool IsPaid => string.Equals(FinancialStatus, "paid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A hash of what matters to the owner (lines, amounts, customer, note). The store also announces
    /// changes that do not matter here (shipping, tags, payment), and those must leave it unchanged.
    /// </summary>
    public string Fingerprint()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = Lines
            .OrderBy(l => l.ExternalLineId, StringComparer.Ordinal)
            .Select(l => $"{l.ExternalLineId}|{l.ExternalProductId}|{l.Title}|{l.VariantTitle}|{l.Quantity}|{l.UnitPrice.ToString("0.00", inv)}");

        var text = string.Join('\n', lines)
            + $"\n#{Total.ToString("0.00", inv)}|{TotalDiscounts.ToString("0.00", inv)}|{Customer}|{Note}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..32];
    }
}

/// <summary>Reads the orders/create webhook body. Anything that is not a usable order gives null.</summary>
public static class ShopifyOrderParser
{
    private const int MaxLines = 100;
    private const decimal MaxMoney = 100_000_000m;

    /// <summary>The order id alone. A deletion webhook carries nothing else, so it cannot go through Parse.</summary>
    public static string? ParseId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Text(doc.RootElement, "id", 40) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
                lines,
                Text(root, "financial_status", 30),
                Discounts(root, items),
                PhoneOf(root));
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

    // The discounts that took money off the items. Shopify lists each one (a coupon, an automatic sale, a
    // manual discount) in discount_applications, and each line says which of them it received. A discount on
    // shipping is not part of the order amount here, so it is skipped. Older payloads only have the codes.
    private static List<IncomingDiscount> Discounts(JsonElement root, JsonElement items)
    {
        var result = new List<IncomingDiscount>();

        if (root.TryGetProperty("discount_applications", out var applications) && applications.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var application in applications.EnumerateArray().Take(20))
            {
                var current = index++;
                if (application.ValueKind != JsonValueKind.Object) continue;
                if (Text(application, "target_type", 30) == "shipping_line") continue;

                var coupon = Text(application, "type", 30) == "discount_code";
                var label = coupon ? Text(application, "code", 60) : Text(application, "title", 100) ?? Text(application, "description", 100);
                if (label is null) continue;

                var lines = new List<string>();
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("discount_allocations", out var allocations) || allocations.ValueKind != JsonValueKind.Array) continue;
                    var title = Text(item, "title", 100) ?? Text(item, "name", 100);
                    if (title is null) continue;

                    foreach (var allocation in allocations.EnumerateArray())
                    {
                        if (allocation.TryGetProperty("discount_application_index", out var at) && at.TryGetInt32(out var i)
                            && i == current && Money(allocation, "amount") is > 0)
                        {
                            lines.Add(title);
                            break;
                        }
                    }
                }

                result.Add(new IncomingDiscount(label, coupon, lines));
            }
        }

        if (result.Count == 0 && root.TryGetProperty("discount_codes", out var codes) && codes.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in codes.EnumerateArray().Take(10))
            {
                if (entry.ValueKind == JsonValueKind.Object && Text(entry, "code", 60) is { } code)
                    result.Add(new IncomingDiscount(code, true, []));
            }
        }

        return result;
    }

    private static string? PhoneOf(JsonElement root)
    {
        if (Text(root, "phone", 30) is { } own) return own;

        foreach (var holder in new[] { "customer", "shipping_address", "billing_address" })
        {
            if (root.TryGetProperty(holder, out var h) && h.ValueKind == JsonValueKind.Object && Text(h, "phone", 30) is { } phone)
                return phone;
        }

        return null;
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
