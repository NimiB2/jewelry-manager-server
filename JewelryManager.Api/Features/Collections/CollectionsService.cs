using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Collections;

/// <summary>
/// Business logic for collections. Every query is scoped to the caller's business.
/// NestJS equivalent: CollectionsService.
/// </summary>
public class CollectionsService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<List<Collection>> GetCollectionsAsync() =>
        await db.Collections
            .Where(c => c.BusinessId == tenant.GetBusinessId())
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

    public async Task<Collection> CreateCollectionAsync(CreateCollectionDto dto)
    {
        var now = DateTime.UtcNow;
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            BusinessId = tenant.GetBusinessId(),
            Name = dto.Name,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Collections.Add(collection);
        await db.SaveChangesAsync();
        return collection;
    }

    public async Task<Collection> RenameCollectionAsync(Guid id, UpdateCollectionDto dto)
    {
        var collection = await FindOrThrowAsync(id);
        collection.Name = dto.Name;
        collection.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return collection;
    }

    /// <summary>
    /// Sets (or clears) the fixed total work hours of a collection. The products already in it take the new
    /// value at once; clearing leaves their stored hours as they are.
    /// </summary>
    public async Task<Collection> SetFixedWorkHoursAsync(Guid id, UpdateCollectionWorkHoursDto dto)
    {
        var collection = await FindOrThrowAsync(id);
        var businessId = tenant.GetBusinessId();

        await using var tx = await db.Database.BeginTransactionAsync();

        collection.FixedWorkHours = dto.FixedWorkHours;
        collection.UpdatedAt = DateTime.UtcNow;

        if (dto.FixedWorkHours is { } total)
        {
            var materialHours = await db.Materials.AsNoTracking()
                .Where(m => m.BusinessId == businessId)
                .ToDictionaryAsync(m => m.Name, m => m.LaborHours);

            var products = await db.Products
                .Where(p => p.BusinessId == businessId && p.Collections.Any(pc => pc.CollectionId == id))
                .ToListAsync();

            foreach (var product in products)
            {
                product.AdditionalWorkHours = Math.Max(0, total - materialHours.GetValueOrDefault(product.Material));
                product.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return collection;
    }

    public async Task<Collection> DeleteCollectionAsync(Guid id)
    {
        var collection = await FindOrThrowAsync(id);
        if (collection.IsPermanent)
            throw new BadRequestException("Cannot delete a permanent collection");

        await using var tx = await db.Database.BeginTransactionAsync();

        await MoveProductsToGeneralAsync(collection);

        // Cascades to the collection's own ProductCollection rows.
        db.Collections.Remove(collection);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return collection;
    }

    // A product must never be left without a collection: the ones that only lived here go to "General".
    private async Task MoveProductsToGeneralAsync(Collection from)
    {
        var businessId = tenant.GetBusinessId();

        var generalId = await db.Collections
            .Where(c => c.BusinessId == businessId && c.Key == "general")
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();
        if (generalId is null) return;

        var alreadyInGeneral = db.ProductCollections
            .Where(pc => pc.CollectionId == generalId)
            .Select(pc => pc.ProductId);

        var toMove = await db.ProductCollections
            .Where(pc => pc.CollectionId == from.Id && !alreadyInGeneral.Contains(pc.ProductId))
            .Select(pc => pc.ProductId)
            .ToListAsync();

        db.ProductCollections.AddRange(toMove.Select(productId => new ProductCollection
        {
            BusinessId = businessId, ProductId = productId, CollectionId = generalId.Value,
        }));
        await db.SaveChangesAsync();
    }

    private async Task<Collection> FindOrThrowAsync(Guid id) =>
        await db.Collections.FirstOrDefaultAsync(
            c => c.Id == id && c.BusinessId == tenant.GetBusinessId())
        ?? throw new NotFoundException("Collection not found");
}
