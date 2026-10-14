using System.Globalization;
using JewelryManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tools;

/// <summary>
/// One-off: gives every order the hourly rate that was true in its time, so each order's salary
/// (work hours x rate) is right and stays frozen when the rate in the settings changes later.
/// Before the "from year" the old rate, from that year until the month before this one the year rate,
/// and this month onward the current rate in the settings.
/// </summary>
public class OrderRatesBackfill(AppDbContext db, Guid businessId)
{
    public record Plan(decimal OldRate, int FromYear, decimal YearRate, DateOnly CurrentMonthStart);

    public record Bucket(string Name, int Orders, decimal Hours, decimal SalaryNow, decimal SalaryAfter);

    public record Result(
        decimal CurrentRate, List<Bucket> Buckets, int OrdersChanged, int IncomesChanged,
        int DeletedOrders, int WithoutHours, List<(int Year, int Month, int Orders, decimal Hours, decimal Salary)> Months);

    public async Task<Result> RunAsync(Plan plan, bool dryRun, string? backupFile = null)
    {
        var currentRate = await db.Settings.AsNoTracking()
            .Where(s => s.BusinessId == businessId).Select(s => s.LaborHourRate).SingleAsync();

        // A projection on purpose: the dry run must work before any new column exists in the database.
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId)
            .Select(o => new
            {
                o.Id, o.Number, o.Date, o.IsDeleted, o.LaborHourRate,
                Hours = o.Items.Sum(i => i.WorkHours * i.Quantity),
                HasIncome = db.Incomes.Any(i => i.OrderId == o.Id),
            })
            .ToListAsync();

        var yearStart = new DateOnly(plan.FromYear, 1, 1);
        decimal RateFor(DateOnly date) =>
            date < yearStart ? plan.OldRate : date < plan.CurrentMonthStart ? plan.YearRate : currentRate;

        string BucketOf(DateOnly date) =>
            date < yearStart ? $"לפני {plan.FromYear}" : date < plan.CurrentMonthStart ? $"{plan.FromYear} עד החודש הקודם" : "החודש הזה";

        // A deleted order earns nothing and is not counted in the figures, but it still gets its rate.
        var counted = rows.Where(r => !r.IsDeleted).ToList();

        var buckets = counted
            .GroupBy(r => BucketOf(r.Date))
            .Select(g => new Bucket(
                g.Key, g.Count(), g.Sum(r => r.Hours),
                g.Sum(r => r.Hours * r.LaborHourRate),
                g.Sum(r => r.Hours * RateFor(r.Date))))
            .OrderBy(b => b.Name)
            .ToList();

        var months = counted
            .Where(r => r.Date >= yearStart)
            .GroupBy(r => (r.Date.Year, r.Date.Month))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key.Year, g.Key.Month, g.Count(), g.Sum(r => r.Hours), g.Sum(r => r.Hours * RateFor(r.Date))))
            .ToList();

        var changing = rows.Where(r => r.LaborHourRate != RateFor(r.Date)).Select(r => r.Id).ToHashSet();
        var incomesToChange = 0;

        if (changing.Count > 0)
        {
            // The old rates are written down first, so the change can be undone if it looks wrong.
            if (!dryRun && backupFile is not null)
            {
                var lines = rows.Where(r => changing.Contains(r.Id))
                    .OrderBy(r => r.Number)
                    .Select(r => $"{r.Number},{r.Date:yyyy-MM-dd},{r.LaborHourRate.ToString(CultureInfo.InvariantCulture)}");
                await File.WriteAllLinesAsync(backupFile, lines.Prepend("order_number,date,old_rate"));
            }

            var orders = await db.Orders.Where(o => o.BusinessId == businessId && changing.Contains(o.Id)).ToListAsync();
            foreach (var order in orders) order.LaborHourRate = RateFor(order.Date);

            var incomes = await db.Incomes.Where(i => i.BusinessId == businessId && i.OrderId != null && changing.Contains(i.OrderId.Value)).ToListAsync();
            var rateByOrder = orders.ToDictionary(o => o.Id, o => o.LaborHourRate);
            foreach (var income in incomes) income.LaborHourRate = rateByOrder[income.OrderId!.Value];
            incomesToChange = incomes.Count;

            if (dryRun) db.ChangeTracker.Clear();
            else await db.SaveChangesAsync();
        }

        return new Result(
            currentRate, buckets, changing.Count, incomesToChange,
            rows.Count(r => r.IsDeleted), counted.Count(r => r.Hours == 0), months);
    }

    public static string Money(decimal value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
