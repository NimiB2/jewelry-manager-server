using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Features.Products.Dtos;

namespace JewelryManager.Api.Features.Shopify.Dtos;

/// <summary>What is connected, so the client can show a clear "not connected yet" instead of an error.</summary>
public record ShopifyStatusResponse(bool CatalogConfigured, bool OrdersConfigured);

/// <summary>A catalog product that could be the match of a store product. Reason is "name" or "price".</summary>
public record MatchCandidateDto(Guid ProductId, string Name, decimal SitePrice, string Reason);

/// <summary>
/// One store product next to what the system knows about it. Status: "linked" (already tied to a catalog
/// product), "match" (one catalog product has exactly this name), "candidates" (some products look right)
/// or "none" (nothing in the catalog resembles it).
/// </summary>
public record StoreProductPreviewDto(
    string ExternalId,
    string Title,
    decimal LowestPrice,
    List<ShopifyVariantDto> Variants,
    string Status,
    Guid? LinkedProductId,
    string? LinkedProductName,
    List<MatchCandidateDto> Candidates);

/// <summary>Body of POST /shopify/products/link. Replace confirms a warning the server raised earlier.</summary>
public record LinkStoreProductDto(
    [Required, MaxLength(40)] string ExternalId,
    Guid ProductId,
    bool Replace = false);

/// <summary>Body of POST /shopify/products/create: the store products to add to the catalog.</summary>
public record CreateFromStoreDto([Required, MinLength(1), MaxLength(500)] List<string> ExternalIds);

public record CreateFromStoreResponse(int Created, List<string> Skipped);
