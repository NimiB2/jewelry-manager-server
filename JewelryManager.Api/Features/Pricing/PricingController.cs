using JewelryManager.Api.Features.Pricing.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Pricing;

[ApiController]
[Route("pricing")]
public class PricingController(PricingService service) : ControllerBase
{
    /// <summary>Live price preview for the calculator; nothing is saved.</summary>
    [HttpPost("calculate")]
    public Task<PriceBreakdown> Calculate(CalculatePriceDto dto) => service.CalculateAsync(dto);
}
