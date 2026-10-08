using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Settings.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Settings;

public class SettingsService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<SettingsResponse> GetSettingsAsync()
    {
        var businessId = tenant.GetBusinessId();

        var settings = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BusinessId == businessId)
            ?? throw new NotFoundException("Settings not found for this business");

        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.BusinessId == businessId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync();

        var fees = await db.FeeItems.AsNoTracking()
            .Where(f => f.BusinessId == businessId)
            .OrderBy(f => f.SortOrder)
            .ToListAsync();

        var categories = await db.PricingAdditionCategories.AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .Include(c => c.Items)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        var stages = await db.PreparationStages.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.Name)
            .ToListAsync();

        var additionTypes = await db.ProductAdditionTypes.AsNoTracking()
            .Where(t => t.BusinessId == businessId)
            .OrderBy(t => t.SortOrder)
            .Select(t => new ProductAdditionTypeDto(t.Name, t.AllowsCustomName))
            .ToListAsync();

        var discountPresets = await db.DiscountPresets.AsNoTracking()
            .Where(d => d.BusinessId == businessId)
            .OrderBy(d => d.SortOrder)
            .Select(d => d.Percent)
            .ToListAsync();

        var expenseTypes = await db.ExpenseTypes.AsNoTracking()
            .Where(t => t.BusinessId == businessId)
            .OrderBy(t => t.SortOrder)
            .Select(t => t.Name)
            .ToListAsync();

        var expenseSuppliers = await db.ExpenseSuppliers.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.Name)
            .ToListAsync();

        var data = new SettingsDataResponse(
            Materials: materials.ToDictionary(
                m => m.Name,
                m => new MaterialSettingsDto(m.PricePerGram, m.LaborHours, m.ProfitMultiplier)),
            LaborHourRate: settings.LaborHourRate,
            PricingAdditions: categories.Select(c => new PricingAdditionDto(
                c.Name,
                c.BasePrice,
                c.Items.OrderBy(i => i.SortOrder).Select(i => new PricingItemDto(i.Name, i.Price)).ToList())).ToList(),
            FeesItems: fees.Select(f => new FeeItemDto(f.Name, f.Percent, f.IsPermanent, f.Key)).ToList(),
            ProfitFloorPercent: settings.ProfitFloorPercent,
            PreparationStages: stages,
            ProductAdditionTypes: additionTypes,
            DiscountPresets: discountPresets,
            TestOrderPrefix: settings.TestOrderPrefix,
            ExpenseTypes: expenseTypes,
            ExpenseSuppliers: expenseSuppliers);

        return new SettingsResponse(settings.Id, settings.BusinessId, data, settings.UpdatedAt);
    }

    public async Task<SettingsResponse> UpdateSettingsAsync(UpdateSettingsDto dto)
    {
        var businessId = tenant.GetBusinessId();
        RejectDuplicateNames(dto);

        await using var tx = await db.Database.BeginTransactionAsync();

        // Settings autosave per section, so PATCHes can overlap. Serializing them per business
        // keeps "delete the section's rows, insert the new ones" from interleaving.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({businessId.ToString()}))");

        var settings = await db.Settings.FirstOrDefaultAsync(s => s.BusinessId == businessId)
            ?? throw new NotFoundException("Settings not found for this business");

        // Only the properties that were sent are assigned, so EF writes only those columns.
        if (dto.LaborHourRate is { } rate) settings.LaborHourRate = rate;
        if (dto.ProfitFloorPercent is { } floor) settings.ProfitFloorPercent = floor;
        if (dto.TestOrderPrefix is not null) settings.TestOrderPrefix = dto.TestOrderPrefix.Trim();
        settings.UpdatedAt = DateTime.UtcNow;

        if (dto.Materials is not null)
        {
            await db.Materials.Where(m => m.BusinessId == businessId).ExecuteDeleteAsync();
            db.Materials.AddRange(dto.Materials.Select((m, i) => new Material
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = m.Key.Trim(),
                PricePerGram = m.Value.PricePerGram, LaborHours = m.Value.LaborHours,
                ProfitMultiplier = m.Value.ProfitMultiplier, SortOrder = i,
            }));
        }

        if (dto.FeesItems is not null)
        {
            await db.FeeItems.Where(f => f.BusinessId == businessId).ExecuteDeleteAsync();
            db.FeeItems.AddRange(dto.FeesItems.Select((f, i) => new FeeItem
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = f.Name.Trim(),
                Percent = f.Percent, IsPermanent = f.IsPermanent ?? false,
                Key = string.IsNullOrWhiteSpace(f.Key) ? null : f.Key, SortOrder = i,
            }));
        }

        if (dto.ProductAdditionTypes is not null)
        {
            await db.ProductAdditionTypes.Where(t => t.BusinessId == businessId).ExecuteDeleteAsync();
            db.ProductAdditionTypes.AddRange(dto.ProductAdditionTypes.Select((t, i) => new ProductAdditionType
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = t.Name.Trim(),
                AllowsCustomName = t.AllowsCustomName, SortOrder = i,
            }));
        }

        if (dto.DiscountPresets is not null)
        {
            await db.DiscountPresets.Where(d => d.BusinessId == businessId).ExecuteDeleteAsync();
            db.DiscountPresets.AddRange(dto.DiscountPresets.Select((percent, i) => new DiscountPreset
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Percent = percent, SortOrder = i,
            }));
        }

        if (dto.PricingAdditions is not null)
        {
            // Deleting a category cascades to its items in the database.
            await db.PricingAdditionCategories.Where(c => c.BusinessId == businessId).ExecuteDeleteAsync();
            db.PricingAdditionCategories.AddRange(dto.PricingAdditions.Select((c, i) => new PricingAdditionCategory
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = c.Name.Trim(), BasePrice = c.BasePrice, SortOrder = i,
                Items = c.Items.Select((item, j) => new PricingAdditionItem
                {
                    Id = Guid.NewGuid(), BusinessId = businessId, Name = item.Name.Trim(), Price = item.Price, SortOrder = j,
                }).ToList(),
            }));
        }

        if (dto.PreparationStages is not null)
        {
            await db.PreparationStages.Where(s => s.BusinessId == businessId).ExecuteDeleteAsync();
            db.PreparationStages.AddRange(dto.PreparationStages.Select((name, i) => new PreparationStage
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = name.Trim(), SortOrder = i,
            }));
        }

        if (dto.ExpenseTypes is not null)
        {
            await db.ExpenseTypes.Where(t => t.BusinessId == businessId).ExecuteDeleteAsync();
            db.ExpenseTypes.AddRange(dto.ExpenseTypes.Select((name, i) => new ExpenseType
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = name.Trim(), SortOrder = i,
            }));
        }

        if (dto.ExpenseSuppliers is not null)
        {
            await db.ExpenseSuppliers.Where(s => s.BusinessId == businessId).ExecuteDeleteAsync();
            db.ExpenseSuppliers.AddRange(dto.ExpenseSuppliers.Select((name, i) => new ExpenseSupplier
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Name = name.Trim(), SortOrder = i,
            }));
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        db.ChangeTracker.Clear();
        return await GetSettingsAsync();
    }

    // The tables enforce unique names too; checking first turns a database error into a clear 400.
    private static void RejectDuplicateNames(UpdateSettingsDto dto)
    {
        if (HasDuplicates(dto.Materials?.Keys)) throw new BadRequestException("Material names must be unique");
        if (HasDuplicates(dto.FeesItems?.Select(f => f.Name))) throw new BadRequestException("Fee names must be unique");
        if (HasDuplicates(dto.PreparationStages)) throw new BadRequestException("Preparation stage names must be unique");
        if (HasDuplicates(dto.ExpenseTypes)) throw new BadRequestException("Expense type names must be unique");
        if (HasDuplicates(dto.ExpenseSuppliers)) throw new BadRequestException("Supplier names must be unique");
        if (dto.ExpenseSuppliers?.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > 100) == true)
            throw new BadRequestException("Supplier names must be 1-100 characters");
        if (dto.ExpenseTypes?.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim().Length > 100) == true)
            throw new BadRequestException("Expense type names must be 1-100 characters");
        if (HasDuplicates(dto.ProductAdditionTypes?.Select(t => t.Name)))
            throw new BadRequestException("Addition names must be unique");
        RejectInvalidFeeKeys(dto.FeesItems);
        if (dto.DiscountPresets?.Any(p => p <= 0 || p > 100) == true)
            throw new BadRequestException("Discount percentages must be between 0.01 and 100");
        if (dto.DiscountPresets is not null && dto.DiscountPresets.Distinct().Count() != dto.DiscountPresets.Count)
            throw new BadRequestException("Discount percentages must be unique");
        if (HasDuplicates(dto.PricingAdditions?.Select(c => c.Name)))
            throw new BadRequestException("Category names must be unique");
        if (dto.PricingAdditions?.Any(c => HasDuplicates(c.Items.Select(i => i.Name))) == true)
            throw new BadRequestException("Item names must be unique within a category");
    }

    // The pricing formula reads these three fees by key, so they can never be removed or duplicated.
    private static void RejectInvalidFeeKeys(List<FeeItemDto>? fees)
    {
        if (fees is null) return;

        var keys = fees.Where(f => !string.IsNullOrWhiteSpace(f.Key)).Select(f => f.Key!).ToList();
        if (keys.Except(FeeKeys.Required).Any())
            throw new BadRequestException("Unknown fee key");
        if (keys.Count != keys.Distinct().Count())
            throw new BadRequestException("A fee key can only be used once");
        if (FeeKeys.Required.Except(keys).Any())
            throw new BadRequestException("The card fee, VAT and fixed-expenses fees cannot be removed");
    }

    private static bool HasDuplicates(IEnumerable<string>? names) =>
        names is not null && names.Select(n => n.Trim()).Distinct().Count() != names.Count();
}
