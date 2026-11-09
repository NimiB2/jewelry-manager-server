using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Analytics;

[ApiController]
[Route("analytics")]
public class AnalyticsController(AnalyticsService service) : ControllerBase
{
    /// <param name="groupBy">day, week or month.</param>
    [HttpGet("sales")]
    public Task<List<SalesRow>> Sales(DateOnly from, DateOnly to, string groupBy = "month") => service.SalesAsync(from, to, groupBy);

    [HttpGet("growth")]
    public Task<GrowthSnapshot> Growth() => service.GrowthAsync();

    [HttpGet("sellers")]
    public Task<SellersResponse> Sellers(int months = 6) => service.SellersAsync(months);

    [HttpGet("anniversaries")]
    public Task<List<AnniversaryRow>> Anniversaries(int daysBack = 365, int window = 7) => service.AnniversariesAsync(daysBack, window);

    [HttpGet("materials")]
    public Task<List<MaterialPriceRow>> Materials() => service.MaterialsAsync();

    [HttpPost("what-if")]
    public Task<WhatIfResponse> WhatIf(WhatIfRequest request) => service.WhatIfAsync(request);

    [HttpGet("discount-ceiling")]
    public Task<DiscountCeilingResponse> DiscountCeiling() => service.DiscountCeilingAsync();
}
