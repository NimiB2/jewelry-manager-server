using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Finances.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Finances;

/// <summary>
/// Read-only insight over the books: where the money went, what the work hours were worth each month,
/// and what sold best. It only reads rows that already exist and never changes them.
/// </summary>
public class FinanceAnalysisService(AppDbContext db, CurrentUserAccessor tenant)
{
    private const string NoGroup = "ללא קבוצה";
    private const string NoCategory = "ללא קטגוריה";

    public async Task<FinanceAnalysisResponse> GetAnalysisAsync(int year, int? month)
    {
        if (year is < 2000 or > 2100) throw new BadRequestException("Invalid year");
        if (month is < 1 or > 12) throw new BadRequestException("Invalid month");

        var businessId = tenant.GetBusinessId();
        var fallbackRate = await db.Settings.AsNoTracking()
            .Where(s => s.BusinessId == businessId).Select(s => s.LaborHourRate).FirstOrDefaultAsync();

        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var (from, to) = Period(year, month);

        // The period before (last month, or last year) is read too, for the comparison.
        var (prevFrom, prevTo) = month is { } m
            ? Period(m == 1 ? year - 1 : year, m == 1 ? 12 : m - 1)
            : Period(year - 1, null);

        // The trend chart shows the six months ending with the chosen month (or the latest month of a whole year).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var trendEndMonth = month ?? (year == today.Year ? today.Month : 12);
        var trendEnd = new DateOnly(year, trendEndMonth, 1);
        var trendStart = trendEnd.AddMonths(-5);

        var earliest = new[] { prevFrom, yearStart, trendStart }.Min();

        var incomes = await db.Incomes.AsNoTracking()
            .Where(i => i.BusinessId == businessId && i.Date >= earliest && i.Date <= yearEnd)
            .ToListAsync();

        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId && e.Date >= earliest && e.Date <= yearEnd)
            .ToListAsync();

        // An expense without its own group takes the default group of its category.
        var typeGroups = await db.ExpenseTypes.AsNoTracking()
            .Where(t => t.BusinessId == businessId && t.GroupName != null)
            .ToDictionaryAsync(t => t.Name, t => t.GroupName!);
        string? GroupOf(Expense e) =>
            !string.IsNullOrWhiteSpace(e.GroupName) ? e.GroupName
            : e.TypeName is not null && typeGroups.TryGetValue(e.TypeName, out var g) ? g : null;

        decimal Salary(Income i) => (i.WorkHours ?? 0) * (i.LaborHourRate ?? fallbackRate);

        var months = Enumerable.Range(1, 12).Select(mo =>
        {
            var inc = incomes.Where(i => i.Date.Year == year && i.Date.Month == mo).ToList();
            var spent = expenses.Where(e => e.Date.Year == year && e.Date.Month == mo).Sum(e => e.Amount);
            return new AnalysisMonth(
                mo,
                inc.Sum(i => i.Amount),
                spent,
                Math.Round(inc.Sum(Salary), 2),
                inc.Sum(i => i.WorkHours ?? 0),
                inc.Count(i => i.OrderId != null));
        }).ToList();

        var periodIncomes = incomes.Where(i => i.Date >= from && i.Date <= to).ToList();
        var periodExpenses = expenses.Where(e => e.Date >= from && e.Date <= to).ToList();
        var previousExpenses = expenses.Where(e => e.Date >= prevFrom && e.Date <= prevTo).ToList();

        var orderIncomes = periodIncomes.Where(i => i.OrderId != null).ToList();
        var hours = periodIncomes.Sum(i => i.WorkHours ?? 0);

        var totals = new AnalysisTotals(
            periodIncomes.Sum(i => i.Amount),
            periodExpenses.Sum(e => e.Amount),
            Math.Round(periodIncomes.Sum(Salary), 2),
            hours,
            orderIncomes.Count,
            orderIncomes.Count == 0 ? 0 : Math.Round(orderIncomes.Sum(i => i.Amount) / orderIncomes.Count, 2),
            hours > 0 ? Math.Round(orderIncomes.Sum(i => i.Amount) / hours, 2) : null,
            periodExpenses.Where(e => e.Category == ExpenseCategory.Fixed).Sum(e => e.Amount),
            periodExpenses.Where(e => e.Category != ExpenseCategory.Fixed).Sum(e => e.Amount),
            periodExpenses.Where(e => KindOf(GroupOf(e)) == GroupKind.Orders).Sum(e => e.Amount),
            periodExpenses.Where(e => KindOf(GroupOf(e)) == GroupKind.Home).Sum(e => e.Amount),
            periodExpenses.Where(e => KindOf(GroupOf(e)) == GroupKind.Business).Sum(e => e.Amount));

        var (topProducts, topMaterials) = await TopSellersAsync(businessId, orderIncomes.Select(i => i.OrderId!.Value).ToList());

        var previous = new AnalysisPrevious(
            incomes.Where(i => i.Date >= prevFrom && i.Date <= prevTo).Sum(i => i.Amount),
            previousExpenses.Sum(e => e.Amount));

        var trend = Enumerable.Range(0, 6).Select(n =>
        {
            var start = trendStart.AddMonths(n);
            var end = new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            return new AnalysisTrendPoint(
                start.Year, start.Month,
                incomes.Where(i => i.Date >= start && i.Date <= end).Sum(i => i.Amount),
                expenses.Where(e => e.Date >= start && e.Date <= end).Sum(e => e.Amount));
        }).ToList();

        var orderSalaries = await OrderSalariesAsync(businessId, orderIncomes, fallbackRate);

        return new FinanceAnalysisResponse(
            year, month, months, totals,
            Slices(periodExpenses, previousExpenses, GroupOf, NoGroup),
            Slices(periodExpenses, previousExpenses, e => e.TypeName, NoCategory),
            Pairs(periodExpenses, previousExpenses, GroupOf),
            topProducts, topMaterials, previous, trend, orderSalaries);
    }

    private enum GroupKind { Orders, Home, Business }

    // The orders and home groups are told apart by name, as the expense form does. Everything else
    // (business, stock, other, no group yet) counts as a cost of the business.
    private static GroupKind KindOf(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return GroupKind.Business;
        if (group.Contains("הזמנות")) return GroupKind.Orders;
        if (group.Contains("בית")) return GroupKind.Home;
        return GroupKind.Business;
    }

    // The biggest earners first; a year can hold hundreds of orders, so the list is capped.
    private async Task<List<AnalysisOrderSalary>> OrderSalariesAsync(Guid businessId, List<Income> orderIncomes, decimal fallbackRate)
    {
        var withHours = orderIncomes.Where(i => (i.WorkHours ?? 0) > 0).ToList();
        if (withHours.Count == 0) return [];

        var ids = withHours.Select(i => i.OrderId!.Value).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && ids.Contains(o.Id))
            .Select(o => new { o.Id, o.Number, o.Customer })
            .ToDictionaryAsync(o => o.Id);

        return withHours
            .Where(i => orders.ContainsKey(i.OrderId!.Value))
            .Select(i =>
            {
                var o = orders[i.OrderId!.Value];
                var rate = i.LaborHourRate ?? fallbackRate;
                return new AnalysisOrderSalary(o.Number, o.Customer, i.WorkHours!.Value, rate, Math.Round(i.WorkHours!.Value * rate, 2));
            })
            .OrderByDescending(s => s.Salary)
            .Take(100)
            .ToList();
    }

    private static (DateOnly From, DateOnly To) Period(int year, int? month) => month is { } m
        ? (new DateOnly(year, m, 1), new DateOnly(year, m, DateTime.DaysInMonth(year, m)))
        : (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    private static List<AnalysisSlice> Slices(
        List<Expense> current, List<Expense> previous, Func<Expense, string?> key, string emptyName)
    {
        string Name(Expense e) => string.IsNullOrWhiteSpace(key(e)) ? emptyName : key(e)!.Trim();

        var total = current.Sum(e => e.Amount);
        var before = previous.GroupBy(Name).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

        return current.GroupBy(Name)
            .Select(g =>
            {
                var amount = g.Sum(e => e.Amount);
                return new AnalysisSlice(
                    g.Key, amount, total > 0 ? Math.Round(amount / total * 100, 1) : 0, g.Count(),
                    before.GetValueOrDefault(g.Key));
            })
            .OrderByDescending(s => s.Amount)
            .ToList();
    }

    private static List<AnalysisPair> Pairs(List<Expense> current, List<Expense> previous, Func<Expense, string?> groupOf)
    {
        string Group(Expense e) => string.IsNullOrWhiteSpace(groupOf(e)) ? NoGroup : groupOf(e)!.Trim();
        static string Category(Expense e) => string.IsNullOrWhiteSpace(e.TypeName) ? NoCategory : e.TypeName.Trim();

        var before = previous.GroupBy(e => (Group(e), Category(e))).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

        return current.GroupBy(e => (Group: Group(e), Category: Category(e)))
            .Select(g => new AnalysisPair(g.Key.Group, g.Key.Category, g.Sum(e => e.Amount), g.Count(), before.GetValueOrDefault(g.Key)))
            .OrderByDescending(p => p.Amount)
            .ToList();
    }

    private async Task<(List<AnalysisTopItem>, List<AnalysisTopItem>)> TopSellersAsync(Guid businessId, List<Guid> orderIds)
    {
        if (orderIds.Count == 0) return ([], []);

        var lines = await db.OrderLineItems.AsNoTracking()
            .Where(l => l.BusinessId == businessId && orderIds.Contains(l.OrderId))
            .Select(l => new Line(l.Name, l.Material, l.UnitPrice, l.Quantity))
            .ToListAsync();

        return (Top(lines, l => l.Name), Top(lines.Where(l => l.Material != ""), l => l.Material));
    }

    private static List<AnalysisTopItem> Top(IEnumerable<Line> lines, Func<Line, string> name) =>
        lines.GroupBy(name)
            .Select(g => new AnalysisTopItem(g.Key, Math.Round(g.Sum(l => l.UnitPrice * l.Quantity), 2), g.Sum(l => l.Quantity)))
            .OrderByDescending(t => t.Revenue)
            .Take(5)
            .ToList();

    private record Line(string Name, string Material, decimal UnitPrice, int Quantity);
}
