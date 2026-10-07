using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Incomes.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Incomes;

public class IncomesService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<IncomeResponse> GetIncomeAsync(Guid id) =>
        ToResponse(await FindOrThrowAsync(id, asNoTracking: true));

    public async Task<IncomeResponse> CreateIncomeAsync(SaveIncomeDto dto)
    {
        var now = DateTime.UtcNow;
        var income = new Income
        {
            Id = Guid.NewGuid(), BusinessId = tenant.GetBusinessId(), CreatedAt = now, UpdatedAt = now,
        };
        Apply(income, dto);

        db.Incomes.Add(income);
        await db.SaveChangesAsync();
        return ToResponse(income);
    }

    public async Task<IncomeResponse> UpdateIncomeAsync(Guid id, SaveIncomeDto dto)
    {
        var income = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfFromOrder(income);

        Apply(income, dto);
        income.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return ToResponse(income);
    }

    public async Task DeleteIncomeAsync(Guid id)
    {
        var income = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfFromOrder(income);

        db.Incomes.Remove(income);
        await db.SaveChangesAsync();
    }

    // Income from an order is a frozen snapshot: it changes only by reopening the order.
    private static void ThrowIfFromOrder(Income income)
    {
        if (income.OrderId is not null)
            throw new BadRequestException("הכנסה מהזמנה נערכת דרך ההזמנה עצמה");
    }

    private static void Apply(Income income, SaveIncomeDto dto)
    {
        var description = dto.Description.Trim();
        if (description.Length == 0) throw new BadRequestException("A description is required");

        income.Date = dto.Date;
        income.Category = dto.Category;
        income.Description = description;
        income.Amount = Math.Round(dto.Amount, 2);
    }

    private async Task<Income> FindOrThrowAsync(Guid id, bool asNoTracking)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Incomes.AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(i => i.Id == id && i.BusinessId == businessId)
            ?? throw new NotFoundException("Income not found");
    }

    private static IncomeResponse ToResponse(Income i) =>
        new(i.Id, i.Date, i.Category, i.Description, i.Amount, i.OrderId);
}
