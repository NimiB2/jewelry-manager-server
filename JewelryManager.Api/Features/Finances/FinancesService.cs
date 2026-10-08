using System.Globalization;
using System.Text;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Finances.Dtos;
using JewelryManager.Api.Features.Incomes;
using JewelryManager.Api.Features.Orders;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Finances;

/// <summary>The combined view: income and expenses in one list, with the period's totals and a CSV export.</summary>
public class FinancesService(AppDbContext db, CurrentUserAccessor tenant, ExpensesService expenses)
{
    public async Task<FinanceListResponse> GetFinancesAsync(DateOnly? from, DateOnly? to, string? type, string? receipt, bool includeProjected = false)
    {
        await PrepareBooksAsync();
        var businessId = tenant.GetBusinessId();

        // Totals cover the whole period; the filters only narrow the list.
        var allInPeriod = await LoadAsync(businessId, from, to);
        var summary = new FinanceSummary(
            allInPeriod.Where(i => i.Kind == FinanceKind.Income).Sum(i => i.Amount),
            allInPeriod.Where(i => i.Kind == FinanceKind.Expense).Sum(i => i.Amount),
            allInPeriod.Sum(i => i.Kind == FinanceKind.Income ? i.Amount : -i.Amount),
            allInPeriod.Count(i => i.Kind == FinanceKind.Expense && !i.HasReceipt));

        var items = Filter(allInPeriod, type, receipt);

        // Coming occurrences are shown for orientation only: they are not money spent yet, so they stay out of the totals.
        if (includeProjected && (receipt is null or "" or "all") && type != "income")
        {
            items.AddRange(await LoadProjectedAsync(businessId, from, to));
            items = items.OrderByDescending(i => i.Date).ThenBy(i => i.Description).ToList();
        }

        return new FinanceListResponse(items, summary);
    }

    public async Task<List<int>> GetYearsAsync()
    {
        await PrepareBooksAsync();
        var businessId = tenant.GetBusinessId();

        var expenseYears = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId).Select(e => e.Date.Year).Distinct().ToListAsync();
        var incomeYears = await db.Incomes.AsNoTracking()
            .Where(i => i.BusinessId == businessId).Select(i => i.Date.Year).Distinct().ToListAsync();

        return expenseYears.Union(incomeYears).OrderByDescending(y => y).ToList();
    }

    public async Task<byte[]> ExportCsvAsync(DateOnly? from, DateOnly? to, string? type, string? receipt)
    {
        var rows = (await GetFinancesAsync(from, to, type, receipt)).Items;

        var csv = new StringBuilder();
        csv.AppendLine("תאריך,סוג,קטגוריה,סוג הוצאה,ספק,תיאור,סכום,חשבונית");
        foreach (var row in rows.OrderBy(r => r.Date))
        {
            csv.AppendLine(string.Join(',',
                row.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                row.Kind == FinanceKind.Income ? "הכנסה" : "הוצאה",
                CategoryLabel(row),
                Escape(row.TypeName ?? ""),
                Escape(row.Supplier ?? ""),
                Escape(row.Description),
                (row.Kind == FinanceKind.Income ? row.Amount : -row.Amount).ToString("0.00", CultureInfo.InvariantCulture),
                row.Kind == FinanceKind.Expense ? (row.HasReceipt ? "יש" : "אין") : ""));
        }

        // The BOM makes Excel read the Hebrew as UTF-8.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    // Recurring expenses and completed orders must be on the books before anything is counted.
    private async Task PrepareBooksAsync()
    {
        await expenses.GenerateDueRecurringAsync(IsraelToday());
        await EnsureIncomeForCompletedOrdersAsync();
    }

    /// <summary>
    /// Orders created before finances existed (or whose income row went missing) get their income here.
    /// Demo orders never count, so test data cannot leak into the books.
    /// </summary>
    private async Task EnsureIncomeForCompletedOrdersAsync()
    {
        var businessId = tenant.GetBusinessId();

        var missing = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.BusinessId == businessId && !o.IsDeleted && !db.Incomes.Any(i => i.OrderId == o.Id))
            .ToListAsync();
        if (missing.Count == 0) return;

        var testPrefix = await db.Settings.AsNoTracking()
            .Where(s => s.BusinessId == businessId).Select(s => s.TestOrderPrefix).FirstOrDefaultAsync();

        foreach (var order in missing.Where(o => !OrdersService.IsTestCustomer(o.Customer, testPrefix)))
            db.Incomes.Add(OrderIncomeFactory.Create(order, order.Items));

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A parallel request created the same income first (unique per order); nothing is missing then.
        }

        db.ChangeTracker.Clear();
    }

    private async Task<List<FinanceItem>> LoadAsync(Guid businessId, DateOnly? from, DateOnly? to)
    {
        var expenseQuery = db.Expenses.AsNoTracking().Where(e => e.BusinessId == businessId);
        var incomeQuery = db.Incomes.AsNoTracking().Where(i => i.BusinessId == businessId);
        if (from is { } f)
        {
            expenseQuery = expenseQuery.Where(e => e.Date >= f);
            incomeQuery = incomeQuery.Where(i => i.Date >= f);
        }

        if (to is { } t)
        {
            expenseQuery = expenseQuery.Where(e => e.Date <= t);
            incomeQuery = incomeQuery.Where(i => i.Date <= t);
        }

        var expenseRows = await expenseQuery.ToListAsync();
        var incomeRows = await incomeQuery.ToListAsync();

        var seriesIds = expenseRows.Where(e => e.SeriesId != null).Select(e => e.SeriesId!.Value).Distinct().ToList();
        var every = await db.RecurringExpenses.AsNoTracking()
            .Where(s => s.BusinessId == businessId && seriesIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.EveryMonths);

        // Both an expense made for an order and an income that came from one show the order's number.
        var orderIds = expenseRows.Where(e => e.OrderId != null).Select(e => e.OrderId!.Value)
            .Concat(incomeRows.Where(i => i.OrderId != null).Select(i => i.OrderId!.Value))
            .Distinct().ToList();
        var orderNumbers = await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Number);

        int? NumberOf(Guid? orderId) => orderId is { } id && orderNumbers.TryGetValue(id, out var n) ? n : null;

        var items = new List<FinanceItem>();
        items.AddRange(expenseRows.Select(e => new FinanceItem(
            FinanceKind.Expense, e.Id, e.Date, e.Description, e.Amount, e.Category, null, e.OrderId,
            e.HasReceipt, e.SeriesId, e.SeriesId is { } sid && every.TryGetValue(sid, out var m) ? m : null,
            e.TypeName, NumberOf(e.OrderId), e.InvoiceStoredName != null, Supplier: e.Supplier, Notes: e.Notes)));
        items.AddRange(incomeRows.Select(i => new FinanceItem(
            FinanceKind.Income, i.Id, i.Date, i.Description, i.Amount, null, i.Category, i.OrderId,
            false, null, null, null, NumberOf(i.OrderId), false)));

        return items.OrderByDescending(i => i.Date).ThenBy(i => i.Description).ToList();
    }

    /// <summary>
    /// The occurrences of recurring expenses that have not happened yet (up to a year ahead), so a coming
    /// month shows what will be charged. They are computed, never stored; generation creates the real
    /// rows when their date arrives.
    /// </summary>
    private async Task<List<FinanceItem>> LoadProjectedAsync(Guid businessId, DateOnly? from, DateOnly? to)
    {
        var today = IsraelToday();
        var horizon = today.AddMonths(12);
        var upTo = to is { } t && t < horizon ? t : horizon;
        if (upTo <= today) return [];

        var series = await db.RecurringExpenses.AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.IsActive)
            .ToListAsync();
        if (series.Count == 0) return [];

        // Tapping a coming occurrence opens the latest real one of its series, where the series is managed.
        var seriesIds = series.Select(s => s.Id).ToList();
        var latest = (await db.Expenses.AsNoTracking()
                .Where(e => e.BusinessId == businessId && e.SeriesId != null && seriesIds.Contains(e.SeriesId.Value))
                .Select(e => new { e.Id, e.SeriesId, e.Date })
                .ToListAsync())
            .GroupBy(e => e.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Date).First().Id);

        var items = new List<FinanceItem>();
        foreach (var s in series.Where(s => latest.ContainsKey(s.Id)))
        {
            for (var date = s.NextDate; date <= upTo; date = ExpensesService.AddMonthsKeepingDay(date, s.EveryMonths, s.DayOfMonth))
            {
                if (date <= today || (from is { } f && date < f)) continue;

                items.Add(new FinanceItem(
                    FinanceKind.Expense, latest[s.Id], date, s.Description, s.Amount, s.Category, null, null,
                    false, s.Id, s.EveryMonths, s.TypeName, null, false, IsProjected: true, Supplier: s.Supplier));
            }
        }

        return items;
    }

    private static List<FinanceItem> Filter(List<FinanceItem> items, string? type, string? receipt)
    {
        IEnumerable<FinanceItem> result = type switch
        {
            null or "" or "all" => items,
            "income" => items.Where(i => i.Kind == FinanceKind.Income),
            "expense" => items.Where(i => i.Kind == FinanceKind.Expense),
            _ => throw new BadRequestException("Unknown type filter"),
        };

        result = receipt switch
        {
            null or "" or "all" => result,
            "missing" => result.Where(i => i.Kind == FinanceKind.Expense && !i.HasReceipt),
            "has" => result.Where(i => i.Kind == FinanceKind.Expense && i.HasReceipt),
            _ => throw new BadRequestException("Unknown receipt filter"),
        };

        return result.ToList();
    }

    private static string CategoryLabel(FinanceItem item) => item.Kind == FinanceKind.Income
        ? item.IncomeCategory == IncomeCategory.Sales ? "מכירות" : "אחר"
        : item.ExpenseCategory == ExpenseCategory.Fixed ? "קבועה" : "חד פעמית";

    // Quotes the cell, and defuses text that Excel would otherwise run as a formula.
    private static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // The books follow the owner's calendar, not the server's UTC clock.
    private static DateOnly IsraelToday()
    {
        foreach (var id in new[] { "Asia/Jerusalem", "Israel Standard Time" })
        {
            try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, id)); }
            catch (TimeZoneNotFoundException) { }
        }

        return DateOnly.FromDateTime(DateTime.UtcNow);
    }
}
