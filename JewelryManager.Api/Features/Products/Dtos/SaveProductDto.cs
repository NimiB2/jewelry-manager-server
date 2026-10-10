using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Products.Dtos;

/// <summary>Body of POST /products and PUT /products/{id}: the product exactly as the calculator saves it.</summary>
public record SaveProductDto(
    [Required, MaxLength(100)] string Type,
    [Required, MaxLength(200)] string Name,
    [Required] string Material,
    // 0 is valid: some products (e.g. bead strings) are priced from their additions only.
    [Range(0, 1_000_000)] decimal Weight,
    [Range(0, 1_000_000)] decimal AdditionalWorkHours,
    [Range(0, 100_000_000)] decimal SitePrice,
    [Required] List<ProductAdditionDto> Additions,
    [Required] List<Guid> CollectionIds,
    // The name in the Shopify store. Null leaves it as it is; an empty text clears it.
    [MaxLength(300)] string? ShopifyName = null);

/// <summary>Body of PATCH /products/{id}/site-price (the quick edit from the product card).</summary>
public record UpdateSitePriceDto([Range(0, 100_000_000)] decimal SitePrice);
