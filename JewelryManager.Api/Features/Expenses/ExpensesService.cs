using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Invoices;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Expenses;

public class ExpensesService(AppDbContext db, CurrentUserAccessor tenant, IInvoiceStorage storage)
{
    /// <summary>Her suppliers list, in her order, for the quick choices on a new expense.</summary>
    public async Task<List<string>> GetSuppliersAsync()
    {
        var businessId = tenant.GetBusinessId();
        return await db.ExpenseSuppliers.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.Name)
            .ToListAsync();
    }

    /// <summary>For each supplier, the expense type it was most often recorded under.</summary>
    public async Task<Dictionary<string, string>> GetSupplierTypesAsync()
    {
        var businessId = tenant.GetBusinessId();
        var rows = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId && e.Supplier != null && e.TypeName != null)
            .GroupBy(e => new { e.Supplier, e.TypeName })
            .Select(g => new { g.Key.Supplier, g.Key.TypeName, Count = g.Count() })
            .ToListAsync();

        return rows
            .GroupBy(r => r.Supplier!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Count).First().TypeName!);
    }

    /// <summary>Moves every expense of the given suppliers to the target and drops the old names from her list.</summary>
    public async Task MergeSuppliersAsync(MergeSuppliersDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var target = dto.Target.Trim();
        var sources = dto.Names.Select(n => n.Trim()).Where(n => n.Length > 0 && n != target).Distinct().ToList();
        if (target.Length == 0 || sources.Count == 0) throw new BadRequestException("Choose a target and at least one other supplier");

        await using var tx = await db.Database.BeginTransactionAsync();

        await db.Expenses
            .Where(e => e.BusinessId == businessId && e.Supplier != null && sources.Contains(e.Supplier))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Supplier, target));
        await db.RecurringExpenses
            .Where(r => r.BusinessId == businessId && r.Supplier != null && sources.Contains(r.Supplier))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Supplier, target));
        await db.ExpenseSuppliers
            .Where(s => s.BusinessId == businessId && sources.Contains(s.Name))
            .ExecuteDeleteAsync();

        await EnsureListedAsync(target);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
    }

    // The same supplier is written differently on different invoices ("X Ltd", "X"), so a name that
    // matches a listed one once spacing, punctuation and company suffixes are ignored takes the listed name.
    private static readonly HashSet<string> CompanySuffixes = ["בע\"מ", "בע״מ", "בע'מ", "בעמ", "ltd", "inc", "llc"];

    public static string SupplierKey(string name)
    {
        var words = name.ToLowerInvariant()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !CompanySuffixes.Contains(w.Trim('.', ',')));
        return new string(string.Concat(words).Where(char.IsLetterOrDigit).ToArray());
    }

    // A supplier typed by hand joins her list in front of the others, ready for the next expense.
    // Returns the name to store: an already listed spelling of the same supplier wins over the new one.
    private async Task<string?> ResolveSupplierAsync(string? supplier)
    {
        if (supplier is null) return null;

        var businessId = tenant.GetBusinessId();
        var existing = await db.ExpenseSuppliers.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .Select(s => new { s.Name, s.SortOrder })
            .ToListAsync();

        var key = SupplierKey(supplier);
        var match = existing.FirstOrDefault(s => string.Equals(s.Name, supplier, StringComparison.OrdinalIgnoreCase))
            ?? (key.Length > 0 ? existing.FirstOrDefault(s => SupplierKey(s.Name) == key) : null);
        if (match is not null) return match.Name;

        await EnsureListedAsync(supplier);
        return supplier;
    }

    private async Task EnsureListedAsync(string supplier)
    {
        var businessId = tenant.GetBusinessId();
        var orders = await db.ExpenseSuppliers.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .Select(s => new { s.Name, s.SortOrder })
            .ToListAsync();
        if (orders.Any(s => s.Name == supplier)) return;

        db.ExpenseSuppliers.Add(new ExpenseSupplier
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Name = supplier,
            SortOrder = orders.Count == 0 ? 0 : orders.Min(s => s.SortOrder) - 1,
        });
    }

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
        var typeName = CleanTypeName(dto.TypeName);
        var supplier = await ResolveSupplierAsync(CleanTypeName(dto.Supplier));

        // An order link belongs to one expense; a recurring series repeats the money, not the order.
        if (dto.RepeatEveryMonths is not null && dto.OrderId is not null)
            throw new BadRequestException("A recurring expense cannot be linked to an order");
        await EnsureOrderExistsAsync(dto.OrderId);

        var expense = new Expense
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Date = dto.Date, Category = dto.Category,
            Description = description, Amount = Math.Round(dto.Amount, 2),
            TypeName = typeName, Supplier = supplier, Notes = CleanTypeName(dto.Notes), OrderId = dto.OrderId, CreatedAt = now, UpdatedAt = now,
            GroupName = CleanTypeName(dto.GroupName),
        };

        if (dto.RepeatEveryMonths is { } every)
        {
            // The first occurrence is the row itself; the series generates the following ones when their date arrives.
            var series = new RecurringExpense
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Category = dto.Category, Description = description,
                TypeName = typeName, Supplier = supplier, GroupName = expense.GroupName, Amount = expense.Amount, EveryMonths = every, DayOfMonth = dto.Date.Day,
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
        var typeName = CleanTypeName(dto.TypeName);
        var supplier = await ResolveSupplierAsync(CleanTypeName(dto.Supplier));
        var amount = Math.Round(dto.Amount, 2);
        var now = DateTime.UtcNow;

        if (dto.OrderId != expense.OrderId) await EnsureOrderExistsAsync(dto.OrderId);

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
            series.TypeName = typeName;
            series.Supplier = supplier;
            series.GroupName = CleanTypeName(dto.GroupName);
            series.Amount = amount;

            var later = await db.Expenses
                .Where(e => e.BusinessId == businessId && e.SeriesId == seriesId && e.Date > expense.Date)
                .ToListAsync();
            foreach (var e in later)
            {
                e.Category = dto.Category;
                e.Description = description;
                e.TypeName = typeName;
                e.Supplier = supplier;
                e.GroupName = CleanTypeName(dto.GroupName);
                e.Amount = amount;
                e.UpdatedAt = now;
            }
        }

        expense.Date = dto.Date;
        expense.Category = dto.Category;
        expense.Description = description;
        expense.TypeName = typeName;
        expense.Supplier = supplier;
        expense.GroupName = CleanTypeName(dto.GroupName);
        expense.Notes = CleanTypeName(dto.Notes);
        expense.OrderId = dto.OrderId;
        expense.Amount = amount;
        expense.UpdatedAt = now;

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetExpenseAsync(id);
    }

    public async Task DeleteExpenseAsync(Guid id, ExpenseDeleteScope scope)
    {
        var businessId = tenant.GetBusinessId();
        var expense = await FindOrThrowAsync(id, asNoTracking: false);
        var doomed = new List<Expense>();

        if (scope == ExpenseDeleteScope.This)
        {
            doomed.Add(expense);
        }
        else
        {
            if (expense.SeriesId is not { } seriesId)
                throw new BadRequestException("This expense is not part of a recurring series");

            // Earlier occurrences are history and always stay. The series is switched off, not deleted,
            // so the ones that stay keep their "recurring" mark and nothing is generated again.
            var later = await db.Expenses
                .Where(e => e.BusinessId == businessId && e.SeriesId == seriesId && e.Date > expense.Date)
                .ToListAsync();
            doomed.AddRange(later);
            if (scope == ExpenseDeleteScope.FromHere) doomed.Add(expense);

            var series = await db.RecurringExpenses.FirstOrDefaultAsync(s => s.Id == seriesId && s.BusinessId == businessId);
            if (series is not null) series.IsActive = false;
        }

        // A statement line that pointed at a deleted expense goes back to pending, so that money is not forgotten.
        await using var tx = await db.Database.BeginTransactionAsync();
        var doomedIds = doomed.Select(e => e.Id).ToList();
        await db.StatementLines
            .Where(l => l.BusinessId == businessId && l.ExpenseId != null && doomedIds.Contains(l.ExpenseId.Value))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, StatementLineStatus.Pending)
                .SetProperty(l => l.ExpenseId, (Guid?)null));

        db.Expenses.RemoveRange(doomed);
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        // The database goes first; a file that fails to delete is left behind rather than blocking the delete.
        foreach (var stored in doomed.Select(e => e.InvoiceStoredName).Where(n => n is not null))
        {
            try { await storage.DeleteAsync(businessId, stored!); }
            catch (IOException) { }
        }
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
                    Description = series.Description, Amount = series.Amount, TypeName = series.TypeName, Supplier = series.Supplier, GroupName = series.GroupName, SeriesId = series.Id,
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

    private static string? CleanTypeName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private async Task EnsureOrderExistsAsync(Guid? orderId)
    {
        if (orderId is not { } id) return;
        var businessId = tenant.GetBusinessId();
        if (!await db.Orders.AsNoTracking().AnyAsync(o => o.Id == id && o.BusinessId == businessId && !o.IsDeleted))
            throw new BadRequestException("Order not found");
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

        int? orderNumber = e.OrderId is { } orderId
            ? await db.Orders.AsNoTracking()
                .Where(o => o.Id == orderId && o.BusinessId == businessId)
                .Select(o => (int?)o.Number)
                .FirstOrDefaultAsync()
            : null;

        return new ExpenseResponse(
            e.Id, e.Date, e.Category, e.Description, e.Amount, e.HasReceipt, e.SeriesId, every,
            e.TypeName, e.OrderId, orderNumber, e.InvoiceFileName, e.Supplier, e.Notes, e.GroupName);
    }
}
