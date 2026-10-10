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
    DateTime UpdatedAt,
    string? ShopifyName,
    // What the store offers for this product (one entry per variant); empty when it was never imported.
    List<ShopifyVariantDto> ShopifyVariants,
    // True while the type or the material is missing (a product imported from the store).
    bool NeedsDetails);

public record ShopifyVariantDto(string Title, decimal Price, string? Sku);

public record ProductsListResponse(List<ProductResponse> Products, PricingMeta? Pricing);
