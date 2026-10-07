using JewelryManager.Api.Features.Pricing.Dtos;

namespace JewelryManager.Api.Features.Products.Dtos;

/// <summary>
/// A product plus its live price. Price is null (with PriceError explaining why) when it can't be
/// computed, e.g. the material was removed from settings — the product itself is still listed.
/// </summary>
public record ProductResponse(
    Guid Id,
    string Type,
    string Name,
    string Material,
    decimal Weight,
    decimal AdditionalWorkHours,
    decimal SitePrice,
    List<ProductAdditionDto> Additions,
    List<Guid> CollectionIds,
    PriceBreakdown? Price,
    string? PriceError,
    DateTime UpdatedAt);

public record ProductsListResponse(List<ProductResponse> Products, PricingMeta? Pricing);
