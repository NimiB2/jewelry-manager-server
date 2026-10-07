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

    public async Task<Collection> DeleteCollectionAsync(Guid id)
    {
        var collection = await FindOrThrowAsync(id);
        if (collection.IsPermanent)
            throw new BadRequestException("Cannot delete a permanent collection");

        // TODO(Products): move this collection's products to the "general" collection
        // before deleting, once Product / ProductCollection exist.
        db.Collections.Remove(collection);
        await db.SaveChangesAsync();
        return collection;
    }

    private async Task<Collection> FindOrThrowAsync(Guid id) =>
        await db.Collections.FirstOrDefaultAsync(
            c => c.Id == id && c.BusinessId == tenant.GetBusinessId())
        ?? throw new NotFoundException("Collection not found");
}
