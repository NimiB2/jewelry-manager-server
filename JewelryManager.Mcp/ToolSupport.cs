using System.Globalization;
using System.Text.Json;

namespace JewelryManager.Mcp;

/// <summary>Small lookups shared by the tool classes.</summary>
internal static class ToolSupport
{
    private static readonly TimeZoneInfo Israel = FindIsrael();

    private static TimeZoneInfo FindIsrael()
    {
        foreach (var id in new[] { "Asia/Jerusalem", "Israel Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }

        return TimeZoneInfo.Utc;
    }

    public static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, Israel));

    /// <summary>Turns "2026-11" into its first and last day. A missing month means "no range".</summary>
    public static (DateOnly? From, DateOnly? To) MonthRange(string? month)
    {
        if (string.IsNullOrWhiteSpace(month)) return (null, null);
        if (!DateTime.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ArgumentException("month must look like 2026-11");

        var first = new DateOnly(parsed.Year, parsed.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}");
        var text = string.Join("&", parts);
        return text.Length == 0 ? "" : "?" + text;
    }

    /// <summary>Finds an order by its number (people say "order 1042", never a GUID).</summary>
    public static async Task<JsonElement> FindOrderAsync(IBackendApi api, int number, CancellationToken ct)
    {
        var list = await api.GetAsync($"/orders{Query(("status", "all"), ("search", number.ToString(CultureInfo.InvariantCulture)))}", ct);
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("orders", out var orders))
        {
            foreach (var order in orders.EnumerateArray())
            {
                if (order.TryGetProperty("number", out var n) && n.TryGetInt32(out var value) && value == number)
                    return order;
            }
        }

        throw new BackendApiException(404, $"Order {number} was not found");
    }

    public static Guid IdOf(JsonElement element) =>
        element.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid)
            ? guid
            : throw new BackendApiException(500, "The system returned no id");

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static decimal Dec(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : 0;

    /// <summary>Finds one catalog product by name. Several matches or none become an error the agent can relay.</summary>
    public static async Task<Guid> FindProductAsync(IBackendApi api, string name, CancellationToken ct)
    {
        var found = await api.GetAsync($"/search{Query(("q", name))}", ct);
        var hits = new List<(Guid Id, string Name)>();
        if (found.ValueKind == JsonValueKind.Object && found.TryGetProperty("products", out var products))
        {
            foreach (var p in products.EnumerateArray())
            {
                var productName = Str(p, "name");
                if (productName is not null && p.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid))
                    hits.Add((guid, productName));
            }
        }

        var exact = hits.Where(h => string.Equals(h.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].Id;
        if (hits.Count == 1) return hits[0].Id;
        if (hits.Count == 0) throw new BackendApiException(404, $"No product named '{name}' was found");

        throw new BackendApiException(409,
            "Several products match: " + string.Join(", ", hits.Take(5).Select(h => h.Name)) + ". Ask which one.");
    }
}
