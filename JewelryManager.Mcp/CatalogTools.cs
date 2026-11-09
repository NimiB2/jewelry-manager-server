using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace JewelryManager.Mcp;

/// <summary>Read-only tools over the catalog, prices, search and bank statements.</summary>
[McpServerToolType]
public class CatalogTools(IBackendApi api)
{
    // Only the final price leaves the system: the cost breakdown and the formula stay hidden.
    private static readonly IReadOnlySet<string> PriceKeys = ResultShaper.Keys("recommendedPrice", "priceExclVat");

    private static readonly IReadOnlySet<string> ProductKeys = ResultShaper.Keys(
        "name", "type", "material", "weight", "sitePrice", "price", "recommendedPrice", "priceError", "needsDetails");

    private static readonly IReadOnlySet<string> SearchKeys = ResultShaper.Keys(
        "query", "orders", "products", "expenses", "number", "customer", "externalName", "date", "finalAmount", "status",
        "name", "type", "material", "sitePrice", "id", "description", "supplier", "amount", "hasInvoice");

    [McpServerTool(Name = "calculate_price", ReadOnly = true)]
    [Description("Recommended price for a piece, from the material name (as in the settings), weight in grams and any extra work hours.")]
    public Task<string> CalculatePrice(string material, decimal weightGrams, decimal extraWorkHours = 0, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var body = new { material, weight = weightGrams, additionalWorkHours = extraWorkHours };
            var data = await api.SendAsync(HttpMethod.Post, "/pricing/calculate", body, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, PriceKeys));
        });

    [McpServerTool(Name = "get_product", ReadOnly = true)]
    [Description("Details and current price of one catalog product, found by name.")]
    public Task<string> GetProduct(string name, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var id = await ToolSupport.FindProductAsync(api, name, ct);
            var data = await api.GetAsync($"/products/{id}", ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, ProductKeys));
        });

    [McpServerTool(Name = "search", ReadOnly = true)]
    [Description("Search orders, products and expenses by a word (at least two characters).")]
    public Task<string> Search(string query, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/search" + ToolSupport.Query(("q", query)), ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, SearchKeys, 5));
        });

    [McpServerTool(Name = "statement_status", ReadOnly = true)]
    [Description("State of a month's bank statement check (yyyy-MM): is it closed, how many lines wait. Counts only, no lines.")]
    public Task<string> StatementStatus(string month, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            ToolSupport.MonthRange(month);
            var data = await api.GetAsync($"/statements/months/{Uri.EscapeDataString(month.Trim())}", ct);

            var counts = new
            {
                month = ToolSupport.Str(data, "month"),
                isClosed = data.TryGetProperty("isClosed", out var closed) && closed.ValueKind == JsonValueKind.True,
                pendingLines = data.TryGetProperty("pendingCount", out var pending) && pending.TryGetInt32(out var p) ? p : 0,
                settlementsWithoutCardStatement = Count(data, "settlementsWithoutCardStatement"),
                expensesNotInStatements = Count(data, "expensesNotInStatements"),
                canClose = data.TryGetProperty("canClose", out var can) && can.ValueKind == JsonValueKind.True,
            };
            return ToolEnvelope.Ok(JsonSerializer.SerializeToNode(counts));
        });

    private static int Count(JsonElement data, string name) =>
        data.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
}
