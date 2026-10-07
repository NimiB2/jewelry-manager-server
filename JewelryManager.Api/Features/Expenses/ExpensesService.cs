using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Expenses;

public class ExpensesService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<ExpenseResponse> GetExpenseAsync(Guid id)
    {
        var expense = await FindOrThrowAsync(id, asNoTracking: true);
        return await ToResponseAsync(expense);
    }

    public async Task<ExpenseResponse> CreateExpenseAsync(CreateExpenseDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var now = DateTime.UtcNow;
        var description = CleanDescription(dto.Description);

        var expense = new Expense
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Date = dto.Date, Category = dto.Category,
            Description = description, Amount = Math.Round(dto.Amount, 2), HasReceipt = dto.HasReceipt,
            CreatedAt = now, UpdatedAt = now,
        };

        if (dto.RepeatEveryMonths is { } every)
        {
            // The first occurrence is the row itself; the series generates the following ones when their date arrives.
            var series = new RecurringExpense
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Category = dto.Category, Description = description,
                Amount = expense.Amount, EveryMonths = every, DayOfMonth = dto.Date.Day,
                NextDate = AddMonthsKeepingDay(dto.Date, every, dto.Date.Day), CreatedAt = now,
            };
            db.RecurringExpenses.Add(series);
            expense.SeriesId = series.Id;
        }

        db.Expenses.Add(expense);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetExpenseAsync(expense.Id);
    }

    public async Task<ExpenseResponse> UpdateExpenseAsync(Guid id, UpdateExpenseDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var expense = await FindOrThrowAsync(id, asNoTracking: false);
        var description = CleanDescription(dto.Description);
        var amount = Math.Round(dto.Amount, 2);
        var now = DateTime.UtcNow;

        if (dto.ApplyToSeries)
        {
            if (expense.SeriesId is not { } seriesId)
                throw new BadRequestException("This expense is not part of a recurring series");

            var series = await db.RecurringExpenses
                .FirstOrDefaultAsync(s => s.Id == seriesId && s.BusinessId == businessId)
                ?? throw new NotFoundException("Recurring series not found");

            // Future occurrences take the new values; earlier ones stay as they were recorded.
            series.Category = dto.Category;
            series.Description = description;
            series.Amount = amount;

            var later = await db.Expenses
                .Where(e => e.BusinessId == businessId && e.SeriesId == seriesId && e.Date > expense.Date)
                .ToListAsync();
            foreach (var e in later)
            {
                e.Category = dto.Category;
                e.Description = description;
                e.Amount = amount;
                e.UpdatedAt = now;
            }
        }

        expense.Date = dto.Date;
        expense.Category = dto.Category;
        expense.Description = description;
        expense.Amount = amount;
        expense.HasReceipt = dto.HasReceipt;
        expense.UpdatedAt = now;

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetExpenseAsync(id);
    }

    public async Task DeleteExpenseAsync(Guid id, bool wholeSeries)
    {
        var businessId = tenant.GetBusinessId();
        var expense = await FindOrThrowAsync(id, asNoTracking: false);

        if (wholeSeries)
        {
            if (expense.SeriesId is not { } seriesId)
                throw new BadRequestException("This expense is not part of a recurring series");

            // Earlier occurrences are history and stay; this one and the later ones go, and the series stops.
            var doomed = await db.Expenses
                .Where(e => e.BusinessId == businessId && e.SeriesId == seriesId && e.Date >= expense.Date)
                .ToListAsync();
            db.Expenses.RemoveRange(doomed);

            var series = await db.RecurringExpenses.FirstOrDefaultAsync(s => s.Id == seriesId && s.BusinessId == businessId);
            if (series is not null) db.RecurringExpenses.Remove(series);
        }
        else
        {
            db.Expenses.Remove(expense);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates every occurrence whose date has arrived. Safe to call on each visit: it runs under a
    /// per-business lock, and each series moves its NextDate forward, so nothing is created twice.
    /// </summary>
    public async Task GenerateDueRecurringAsync(DateOnly today)
    {
        var businessId = tenant.GetBusinessId();

        await using var tx = await db.Database.BeginTransactionAsync();
        var lockKey = $"recurring:{businessId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))");

        var due = await db.RecurringExpenses
            .Where(s => s.BusinessId == businessId && s.IsActive && s.NextDate <= today)
            .ToListAsync();
        if (due.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var series in due)
        {
            while (series.NextDate <= today)
            {
                db.Expenses.Add(new Expense
                {
                    Id = Guid.NewGuid(), BusinessId = businessId, Date = series.NextDate, Category = series.Category,
                    Description = series.Description, Amount = series.Amount, SeriesId = series.Id,
                    CreatedAt = now, UpdatedAt = now,
                });
                series.NextDate = AddMonthsKeepingDay(series.NextDate, series.EveryMonths, series.DayOfMonth);
            }
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
    }

    // Jan 31 + 1 month is Feb 28, and the month after that goes back to Mar 31 instead of drifting to the 28th.
    internal static DateOnly AddMonthsKeepingDay(DateOnly from, int months, int day)
    {
        var first = new DateOnly(from.Year, from.Month, 1).AddMonths(months);
        return new DateOnly(first.Year, first.Month, Math.Min(day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    private static string CleanDescription(string description)
    {
        var clean = description.Trim();
        if (clean.Length == 0) throw new BadRequestException("A description is required");
        return clean;
    }

    private async Task<Expense> FindOrThrowAsync(Guid id, bool asNoTracking)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Expenses.AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(e => e.Id == id && e.BusinessId == businessId)
            ?? throw new NotFoundException("Expense not found");
    }

    private async Task<ExpenseResponse> ToResponseAsync(Expense e)
    {
        var businessId = tenant.GetBusinessId();
        int? every = e.SeriesId is { } seriesId
            ? await db.RecurringExpenses.AsNoTracking()
                .Where(s => s.Id == seriesId && s.BusinessId == businessId)
                .Select(s => (int?)s.EveryMonths)
                .FirstOrDefaultAsync()
            : null;

        return new ExpenseResponse(e.Id, e.Date, e.Category, e.Description, e.Amount, e.HasReceipt, e.SeriesId, every);
    }
}
