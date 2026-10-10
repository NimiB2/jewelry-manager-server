using System.Text.Json;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Pricing.Dtos;
using JewelryManager.Api.Features.Products.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Products;

public class ProductsService(AppDbContext db, CurrentUserAccessor tenant, PricingService pricing)
{
    public async Task<ProductsListResponse> GetProductsAsync()
    {
        var businessId = tenant.GetBusinessId();

        var products = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId)
            .Include(p => p.Additions)
            .Include(p => p.Collections)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var (settings, settingsError) = await TryLoadSettingsAsync();
        var items = products.Select(p => ToResponse(p, settings, settingsError)).ToList();
        return new ProductsListResponse(items, settings?.ToMeta());
    }

    public async Task<ProductResponse> GetProductAsync(Guid id)
    {
        var product = await FindOrThrowAsync(id, asNoTracking: true);
        var (settings, settingsError) = await TryLoadSettingsAsync();
        return ToResponse(product, settings, settingsError);
    }

    public async Task<ProductResponse> CreateProductAsync(SaveProductDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var additions = await ValidateAdditionsAsync(dto.Additions);
        var collectionIds = await ResolveCollectionIdsAsync(dto.CollectionIds);
        await EnsureShopifyNameFreeAsync(dto.ShopifyName, null);

        var now = DateTime.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CreatedAt = now, UpdatedAt = now,
            Collections = collectionIds
                .Select(cid => new ProductCollection { BusinessId = businessId, CollectionId = cid })
                .ToList(),
        };
        Apply(product, dto, additions);
        product.Additions = additions;

        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetProductAsync(product.Id);
    }

    public async Task<ProductResponse> UpdateProductAsync(Guid id, SaveProductDto dto)
    {
        var product = await FindOrThrowAsync(id, asNoTracking: false);
        var additions = await ValidateAdditionsAsync(dto.Additions);
        var collectionIds = (await ResolveCollectionIdsAsync(dto.CollectionIds)).ToHashSet();
        await EnsureShopifyNameFreeAsync(dto.ShopifyName, id);

        // Only the difference is applied: re-adding an unchanged link in the same save would clash on its key.
        foreach (var link in product.Collections.Where(l => !collectionIds.Contains(l.CollectionId)).ToList())
            product.Collections.Remove(link);
        foreach (var cid in collectionIds.Where(cid => product.Collections.All(l => l.CollectionId != cid)))
            product.Collections.Add(new ProductCollection { BusinessId = product.BusinessId, CollectionId = cid });

        db.ProductAdditions.RemoveRange(product.Additions);
        Apply(product, dto, additions);
        product.UpdatedAt = DateTime.UtcNow;

        // Added explicitly: through the navigation EF would treat objects that already have an Id as existing rows.
        foreach (var addition in additions)
        {
            addition.ProductId = product.Id;
            db.ProductAdditions.Add(addition);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetProductAsync(id);
    }

    public async Task<ProductResponse> UpdateSitePriceAsync(Guid id, UpdateSitePriceDto dto)
    {
        var product = await FindOrThrowAsync(id, asNoTracking: false);
        product.SitePrice = dto.SitePrice;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetProductAsync(id);
    }

    public async Task DeleteProductAsync(Guid id)
    {
        var product = await FindOrThrowAsync(id, asNoTracking: false);
        db.Products.Remove(product);
        await db.SaveChangesAsync();
    }

    private static void Apply(Product product, SaveProductDto dto, List<ProductAddition> additions)
    {
        product.Type = dto.Type.Trim();
        product.Name = dto.Name.Trim();
        product.Material = dto.Material;
        product.Weight = dto.Weight;
        product.AdditionalWorkHours = dto.AdditionalWorkHours;
        product.SitePrice = dto.SitePrice;

        // Null leaves the store name alone, so a client that does not know the field never wipes it.
        if (dto.ShopifyName is not null)
            product.ShopifyName = string.IsNullOrWhiteSpace(dto.ShopifyName) ? null : dto.ShopifyName.Trim();

        foreach (var addition in additions)
            addition.BusinessId = product.BusinessId;
    }

    // Two catalog products with the same store name would make incoming orders ambiguous.
    private async Task EnsureShopifyNameFreeAsync(string? shopifyName, Guid? exceptProductId)
    {
        var name = shopifyName?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var businessId = tenant.GetBusinessId();
        var lowered = name.ToLower();
        var taken = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId && p.Id != exceptProductId && p.ShopifyName != null && p.ShopifyName.ToLower() == lowered)
            .Select(p => p.Name)
            .FirstOrDefaultAsync();

        if (taken is not null)
            throw new BadRequestException($"השם \"{name}\" בשופיפי כבר מקושר למוצר \"{taken}\"");
    }

    private async Task<List<ProductAddition>> ValidateAdditionsAsync(List<ProductAdditionDto> dtos)
    {
        if (dtos.Count == 0) return [];

        var businessId = tenant.GetBusinessId();
        var types = await db.ProductAdditionTypes.AsNoTracking()
            .Where(t => t.BusinessId == businessId)
            .ToDictionaryAsync(t => t.Name);

        return dtos.Select((a, i) =>
        {
            if (!types.TryGetValue(a.TypeName, out var type))
                throw new BadRequestException($"Unknown addition type '{a.TypeName}'");

            var customName = a.CustomName?.Trim();
            if (type.AllowsCustomName && string.IsNullOrEmpty(customName))
                throw new BadRequestException($"A name is required for the addition '{a.TypeName}'");

            return new ProductAddition
            {
                Id = Guid.NewGuid(), TypeName = type.Name,
                CustomName = type.AllowsCustomName ? customName : null,
                Price = a.Price, Quantity = a.Quantity, SortOrder = i,
            };
        }).ToList();
    }

    // Every product lives in at least one collection; with none chosen it goes to "General".
    private async Task<List<Guid>> ResolveCollectionIdsAsync(List<Guid> requested)
    {
        var businessId = tenant.GetBusinessId();
        var ids = requested.Distinct().ToList();

        if (ids.Count == 0)
        {
            var generalId = await db.Collections
                .Where(c => c.BusinessId == businessId && c.Key == "general")
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync();
            return generalId is { } g ? [g] : [];
        }

        var found = await db.Collections
            .Where(c => c.BusinessId == businessId && ids.Contains(c.Id))
            .CountAsync();
        if (found != ids.Count)
            throw new BadRequestException("One or more collections do not exist");

        return ids;
    }

    private async Task<Product> FindOrThrowAsync(Guid id, bool asNoTracking)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Products
            .Include(p => p.Additions)
            .Include(p => p.Collections)
            .AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(p => p.Id == id && p.BusinessId == businessId)
            ?? throw new NotFoundException("Product not found");
    }

    private async Task<(PricingSettings? Settings, string? Error)> TryLoadSettingsAsync()
    {
        try
        {
            return (await pricing.LoadSettingsAsync(), null);
        }
        catch (AppException ex)
        {
            // The product list must still work when pricing can't (yet) be computed.
            return (null, ex.Message);
        }
    }

    private static ProductResponse ToResponse(Product p, PricingSettings? settings, string? settingsError)
    {
        var additions = p.Additions.OrderBy(a => a.SortOrder)
            .Select(a => new ProductAdditionDto(a.TypeName, a.CustomName, a.Price, a.Quantity))
            .ToList();

        PriceBreakdown? price = null;
        var error = settingsError;
        if (settings is not null)
        {
            try
            {
                price = PricingService.Calculate(
                    settings, p.Material, p.Weight, p.AdditionalWorkHours, PricingService.AdditionsCost(additions));
            }
            catch (BadRequestException ex)
            {
                error = ex.Message;
            }
        }

        return new ProductResponse(
            p.Id, p.Type, p.Name, p.Material, p.Weight, p.AdditionalWorkHours, p.SitePrice,
            additions, p.Collections.Select(c => c.CollectionId).ToList(), price, error, p.UpdatedAt,
            p.ShopifyName, ReadVariants(p.ShopifyVariants), p.NeedsDetails);
    }

    // The variants are display-only data written by the importer; anything unreadable just shows nothing.
    private static List<ShopifyVariantDto> ReadVariants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ShopifyVariantDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
