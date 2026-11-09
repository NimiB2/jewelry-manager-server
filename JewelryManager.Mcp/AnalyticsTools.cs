using System.ComponentModel;
using ModelContextProtocol.Server;

namespace JewelryManager.Mcp;

/// <summary>Read-only views of how the business is doing, and what a change in prices or a discount would do.</summary>
[McpServerToolType]
public class AnalyticsTools(IBackendApi api)
{
    private static readonly IReadOnlySet<string> SalesKeys = ResultShaper.Keys("period", "orders", "revenue");

    private static readonly IReadOnlySet<string> GrowthKeys = ResultShaper.Keys(
        "ordersLast30Days", "ordersPrior30DayAverage", "averageOrderValue90Days", "repeatCustomerRate", "products",
        "productsNeedingDetails", "daysSinceLastProductAdded", "storeShare90Days", "topMaterial", "topMaterialShare");

    private static readonly IReadOnlySet<string> SellerKeys = ResultShaper.Keys(
        "months", "best", "slow", "name", "quantity", "revenue", "type", "daysInCatalog");

    private static readonly IReadOnlySet<string> CeilingKeys = ResultShaper.Keys(
        "profitFloorPercent", "byType", "type", "products", "safeForAllPercent", "medianPercent", "alreadyBelowFloor");

    private static readonly IReadOnlySet<string> WhatIfKeys = ResultShaper.Keys(
        "products", "averageChangePercent", "byType", "type", "newlyBelowProfitFloor", "newlyBelowExamples");

    [McpServerTool(Name = "sales_by_period", ReadOnly = true)]
    [Description("Orders and revenue over time. from and to are yyyy-MM-dd; groupBy is day, week or month (default month).")]
    public Task<string> SalesByPeriod(string from, string to, string? groupBy = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/analytics/sales" + ToolSupport.Query(("from", from), ("to", to), ("groupBy", groupBy ?? "month")), ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, SalesKeys, 24));
        });

    [McpServerTool(Name = "growth_snapshot", ReadOnly = true)]
    [Description("Signals about the business: recent orders against before, average order, repeat customers, new products, store share.")]
    public Task<string> GrowthSnapshot(CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () => ToolEnvelope.Ok(ResultShaper.Shape(await api.GetAsync("/analytics/growth", ct), GrowthKeys)));

    [McpServerTool(Name = "best_and_slow_sellers", ReadOnly = true)]
    [Description("The best-selling items of the last N months (default 6) and the products that have not sold in that time.")]
    public Task<string> BestAndSlowSellers(int months = 6, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () => ToolEnvelope.Ok(ResultShaper.Shape(await api.GetAsync($"/analytics/sellers?months={months}", ct), SellerKeys, 8)));

    [McpServerTool(Name = "discount_ceiling", ReadOnly = true)]
    [Description("The largest discount, per product type, that still keeps the profit floor, and the products already below it.")]
    public Task<string> DiscountCeiling(CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () => ToolEnvelope.Ok(ResultShaper.Shape(await api.GetAsync("/analytics/discount-ceiling", ct), CeilingKeys, 12)));

    [McpServerTool(Name = "what_if_metal", ReadOnly = true)]
    [Description("What would the recommended prices do if a material's price per gram changed by this percent (for example 5 or -3)? Nothing is changed.")]
    public Task<string> WhatIfMetal(string material, decimal percent, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var body = new { changes = new[] { new { material, percent } } };
            var data = await api.SendAsync(HttpMethod.Post, "/analytics/what-if", body, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, WhatIfKeys, 6));
        });
}
