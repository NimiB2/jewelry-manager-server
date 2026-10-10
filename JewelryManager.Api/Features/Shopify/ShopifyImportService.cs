using System.Text.Json;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Products.Dtos;
using JewelryManager.Api.Features.Shopify.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>
/// Brings the store's existing products next to the catalog: shows which are already tied to a product,
/// suggests matches, links one, or adds the missing ones. Nothing is ever written back to the store.
/// </summary>
public class ShopifyImportService(
    AppDbContext db, CurrentUserAccessor tenant, IShopifyCatalogClient catalog, IOptions<ShopifyOptions> options)
{
    private const int MaxPriceCandidates = 5;

    public ShopifyStatusResponse GetStatus() => new(options.Value.CanReadCatalog, options.Value.CanReceiveOrders);

    public async Task<List<StoreProductPreviewDto>> PreviewAsync()
    {
        var businessId = tenant.GetBusinessId();
        var store = await catalog.GetActiveProductsAsync();

        var products = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId)
            .Select(p => new { p.Id, p.Name, p.ShopifyName, p.ShopifyProductId, p.SitePrice })
            .ToListAsync();

        return store.Select(sp =>
        {
            var variants = sp.Variants.Select(v => new ShopifyVariantDto(v.Title, v.Price, v.Sku)).ToList();

            var linked = products.FirstOrDefault(p => p.ShopifyProductId == sp.ExternalId);
            if (linked is not null)
                return new StoreProductPreviewDto(sp.ExternalId, sp.Title, sp.LowestPrice, variants, "linked", linked.Id, linked.Name, []);

            var title = Normalize(sp.Title);
            var free = products.Where(p => p.ShopifyProductId is null).ToList();

            var byName = free
                .Where(p => Normalize(p.ShopifyName) == title || Normalize(p.Name) == title)
                .Select(p => new MatchCandidateDto(p.Id, p.Name, p.SitePrice, "name"))
                .ToList();

            var prices = sp.Variants.Select(v => v.Price).ToHashSet();
            var byPrice = free
                .Where(p => p.SitePrice > 0 && prices.Contains(p.SitePrice) && byName.All(c => c.ProductId != p.Id))
                .OrderBy(p => p.Name)
                .Take(MaxPriceCandidates)
                .Select(p => new MatchCandidateDto(p.Id, p.Name, p.SitePrice, "price"))
                .ToList();

            var candidates = byName.Concat(byPrice).ToList();
            var status = byName.Count == 1 ? "match" : candidates.Count > 0 ? "candidates" : "none";
            return new StoreProductPreviewDto(sp.ExternalId, sp.Title, sp.LowestPrice, variants, status, null, null, candidates);
        }).ToList();
    }

    /// <summary>
    /// Ties a store product to a catalog product. When either side is already tied to something else the
    /// server answers with a conflict (a warning); the same call with Replace = true confirms it.
    /// </summary>
    public async Task LinkAsync(LinkStoreProductDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var store = (await catalog.GetActiveProductsAsync()).FirstOrDefault(p => p.ExternalId == dto.ExternalId)
            ?? throw new NotFoundException("המוצר לא נמצא בחנות");

        await using var tx = await db.Database.BeginTransactionAsync();

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == dto.ProductId && p.BusinessId == businessId)
            ?? throw new NotFoundException("Product not found");

        var title = store.Title.ToLowerInvariant();
        var others = await db.Products
            .Where(p => p.BusinessId == businessId && p.Id != product.Id
                && (p.ShopifyProductId == store.ExternalId || (p.ShopifyName != null && p.ShopifyName.ToLower() == title)))
            .ToListAsync();

        if (!dto.Replace && product.ShopifyProductId is not null && product.ShopifyProductId != store.ExternalId)
            throw new ConflictException($"המוצר \"{product.Name}\" כבר מקושר למוצר \"{product.ShopifyName}\" בשופיפי", "PRODUCT_ALREADY_LINKED");

        if (!dto.Replace && others.Count > 0)
            throw new ConflictException($"המוצר \"{store.Title}\" בשופיפי כבר מקושר ל-\"{others[0].Name}\"", "STORE_PRODUCT_ALREADY_LINKED");

        // The old owner lets go first: the store id is unique, and the check runs per statement.
        foreach (var other in others)
        {
            other.ShopifyProductId = null;
            other.ShopifyName = null;
            other.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        Apply(product, store);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Adds store products to the catalog with only a name and a price; the rest is filled in later.</summary>
    public async Task<CreateFromStoreResponse> CreateAsync(CreateFromStoreDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var wanted = dto.ExternalIds.ToHashSet();
        var store = (await catalog.GetActiveProductsAsync()).Where(p => wanted.Contains(p.ExternalId)).ToList();

        var existing = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId && (p.ShopifyProductId != null || p.ShopifyName != null))
            .Select(p => new { p.ShopifyProductId, p.ShopifyName })
            .ToListAsync();
        var takenIds = existing.Where(p => p.ShopifyProductId is not null).Select(p => p.ShopifyProductId!).ToHashSet();
        var takenNames = existing.Where(p => p.ShopifyName is not null).Select(p => Normalize(p.ShopifyName)).ToHashSet();

        var general = await db.Collections.AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.Key == "general")
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();

        var created = 0;
        var skipped = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var sp in store)
        {
            // Already in the catalog (by id, or by a name that would make orders ambiguous): leave it for linking.
            if (takenIds.Contains(sp.ExternalId) || takenNames.Contains(Normalize(sp.Title)))
            {
                skipped.Add(sp.Title);
                continue;
            }

            var product = new Product
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CreatedAt = now, UpdatedAt = now,
                Name = sp.Title.Length > 200 ? sp.Title[..200] : sp.Title,
                SitePrice = sp.LowestPrice,
                Collections = general is { } g ? [new ProductCollection { BusinessId = businessId, CollectionId = g }] : [],
            };
            Apply(product, sp);

            db.Products.Add(product);
            takenIds.Add(sp.ExternalId);
            takenNames.Add(Normalize(sp.Title));
            created++;
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new CreateFromStoreResponse(created, skipped);
    }

    // The tie itself: the store id, the store name and the variants shown for comparison.
    private static void Apply(Product product, StoreProduct store)
    {
        product.ShopifyProductId = store.ExternalId;
        product.ShopifyName = store.Title;
        product.ShopifyVariants = JsonSerializer.Serialize(store.Variants.Select(v => new ShopifyVariantDto(v.Title, v.Price, v.Sku)));
        product.UpdatedAt = DateTime.UtcNow;
    }

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
