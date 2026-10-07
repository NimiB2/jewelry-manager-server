using System.Text;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Finances;
using JewelryManager.Api.Features.Finances.Dtos;
using JewelryManager.Api.Features.Incomes;
using JewelryManager.Api.Features.Incomes.Dtos;
using JewelryManager.Api.Features.Orders;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>Finances against real PostgreSQL. Each test uses its own business so rows never mix.</summary>
[Collection("postgres")]
public class FinancesTests(PostgresFixture pg)
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private async Task<T> WithExpenses<T>(Guid business, Func<ExpensesService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new ExpensesService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task GenerateAsync(Guid business, DateOnly today)
    {
        await using var db = pg.NewDb();
        await new ExpensesService(db, PostgresFixture.TenantFor(business)).GenerateDueRecurringAsync(today);
    }

    private async Task DeleteExpenseAsync(Guid business, Guid id, bool wholeSeries)
    {
        await using var db = pg.NewDb();
        await new ExpensesService(db, PostgresFixture.TenantFor(business)).DeleteExpenseAsync(id, wholeSeries);
    }

    private async Task<T> WithIncomes<T>(Guid business, Func<IncomesService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new IncomesService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task<T> WithFinances<T>(Guid business, Func<FinancesService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(business);
        return await action(new FinancesService(db, tenant, new ExpensesService(db, tenant)));
    }

    private static CreateExpenseDto Expense(DateOnly date, decimal amount, string description = "חומרים",
        bool receipt = false, int? repeat = null) =>
        new(date, ExpenseCategory.Variable, description, amount, receipt, repeat);

    [Fact]
    public async Task Summary_CoversThePeriod_AndFiltersOnlyNarrowTheList()
    {
        var b = pg.BusinessE;
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2025, 3, 5), 100, receipt: true)));
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2025, 3, 9), 50)));
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2025, 4, 1), 999)));
        await WithIncomes(b, s => s.CreateIncomeAsync(new SaveIncomeDto(D(2025, 3, 7), IncomeCategory.Other, "שיעור", 400)));

        var march = await WithFinances(b, s => s.GetFinancesAsync(D(2025, 3, 1), D(2025, 3, 31), null, null));
        Assert.Equal(3, march.Items.Count);
        Assert.Equal(400, march.Summary.TotalIncome);
        Assert.Equal(150, march.Summary.TotalExpenses);
        Assert.Equal(250, march.Summary.NetProfit);
        Assert.Equal(1, march.Summary.ExpensesWithoutReceipt);

        var missing = await WithFinances(b, s => s.GetFinancesAsync(D(2025, 3, 1), D(2025, 3, 31), null, "missing"));
        Assert.Single(missing.Items);
        Assert.Equal(50, missing.Items[0].Amount);
        // The totals ignore the list filter.
        Assert.Equal(250, missing.Summary.NetProfit);

        var incomeOnly = await WithFinances(b, s => s.GetFinancesAsync(null, null, "income", null));
        Assert.Single(incomeOnly.Items);

        Assert.Contains(2025, await WithFinances(b, s => s.GetYearsAsync()));
    }

    [Fact]
    public async Task OtherBusinesses_NeverSeeTheRows()
    {
        var created = await WithExpenses(pg.BusinessE, s => s.CreateExpenseAsync(Expense(D(2024, 1, 1), 10, "פרטי")));

        var foreign = await WithFinances(pg.BusinessF, s => s.GetFinancesAsync(D(2024, 1, 1), D(2024, 12, 31), null, null));
        Assert.Empty(foreign.Items);
        await Assert.ThrowsAsync<NotFoundException>(() => WithExpenses(pg.BusinessF, s => s.GetExpenseAsync(created.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => DeleteExpenseAsync(pg.BusinessF, created.Id, false));
    }

    [Fact]
    public async Task RecurringExpense_IsGeneratedOnceWhenItsDateArrives_AndKeepsTheDayOfMonth()
    {
        var b = pg.BusinessE;
        var first = await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2023, 1, 31), 1500, "שכירות", repeat: 1)));
        Assert.Equal(1, first.RepeatEveryMonths);

        var today = D(2023, 4, 15);
        // Visiting the screen repeatedly must not create duplicates.
        await GenerateAsync(b, today);
        await GenerateAsync(b, today);

        await using var db = pg.NewDb();
        var dates = await db.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).Select(e => e.Date).ToListAsync();
        Assert.Equal([D(2023, 1, 31), D(2023, 2, 28), D(2023, 3, 31)], dates);

        // Stop the series so it does not keep producing rows for the other tests in this business.
        await DeleteExpenseAsync(b, first.Id, true);
    }

    [Fact]
    public async Task RecurringExpense_DeletingOneOccurrenceDoesNotRegenerateIt_AndEditingTheSeriesSkipsThePast()
    {
        var b = pg.BusinessE;
        var first = await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2022, 1, 10), 100, "ארנונה", repeat: 1)));
        await GenerateAsync(b, D(2022, 4, 10));

        await using (var db = pg.NewDb())
        {
            var rows = await db.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).ToListAsync();
            Assert.Equal(4, rows.Count);

            // Remove the latest occurrence: it must stay removed.
            await DeleteExpenseAsync(b, rows[3].Id, false);
        }

        await GenerateAsync(b, D(2022, 4, 20));
        await using (var db = pg.NewDb())
        {
            var rows = await db.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).ToListAsync();
            Assert.Equal(3, rows.Count); // Jan, Feb, Mar; the deleted April is not back
            Assert.DoesNotContain(rows, r => r.Date == D(2022, 4, 10));

            // New amount from February onwards; January stays as it was recorded.
            var feb = rows.Single(r => r.Date == D(2022, 2, 10));
            await WithExpenses(b, s => s.UpdateExpenseAsync(feb.Id,
                new UpdateExpenseDto(feb.Date, ExpenseCategory.Fixed, "ארנונה", 120, false, ApplyToSeries: true)));
        }

        await using (var db = pg.NewDb())
        {
            var rows = await db.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).ToListAsync();
            Assert.Equal(100, rows[0].Amount);
            Assert.All(rows.Skip(1), r => Assert.Equal(120, r.Amount));

            // Delete the whole series from March: January and February remain, the series is gone.
            var mar = rows.Single(r => r.Date == D(2022, 3, 10));
            await DeleteExpenseAsync(b, mar.Id, true);
        }

        await GenerateAsync(b, D(2023, 1, 1));
        await using var final = pg.NewDb();
        Assert.Equal(2, await final.Expenses.CountAsync(e => e.BusinessId == b && e.Description == "ארנונה"));
    }

    [Fact]
    public async Task IncomeFromAnOrder_IsBookedOnCompletion_ReadOnly_AndRemovedOnReopen()
    {
        var b = pg.BusinessF;
        var orderId = Guid.NewGuid();
        await using (var db = pg.NewDb())
        {
            var now = DateTime.UtcNow;
            db.Settings.Add(new Settings { Id = Guid.NewGuid(), BusinessId = b, TestOrderPrefix = "בדיקה", CreatedAt = now, UpdatedAt = now });
            db.Orders.AddRange(
                new Order
                {
                    Id = orderId, BusinessId = b, Number = 1000, Customer = "דנה", Date = D(2026, 5, 1),
                    Amount = 900, FinalAmount = 800, ReceiptSent = true, Status = OrderStatus.Ready,
                    LaborHourRate = 100, CreatedAt = now, UpdatedAt = now,
                    Items = [new OrderLineItem { Id = Guid.NewGuid(), BusinessId = b, Name = "טבעת", Type = "טבעת", Material = "כסף", UnitPrice = 900, Quantity = 2, WorkHours = 1.5m }],
                },
                new Order
                {
                    Id = Guid.NewGuid(), BusinessId = b, Number = 500, Customer = "בדיקה דנה", Date = D(2026, 5, 1),
                    Amount = 50, FinalAmount = 50, ReceiptSent = true, Status = OrderStatus.Completed, IsCompleted = true,
                    CompletedDate = DateTime.UtcNow, CreatedAt = now, UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        // The demo order never reaches the books, even though it is completed.
        Assert.Empty((await WithFinances(b, s => s.GetFinancesAsync(null, null, null, null))).Items);

        await using (var db = pg.NewDb())
        {
            var orders = new OrdersService(db, PostgresFixture.TenantFor(b));
            await orders.UpdateStatusAsync(orderId, OrderStatus.Completed);
        }

        var list = await WithFinances(b, s => s.GetFinancesAsync(null, null, null, null));
        var row = Assert.Single(list.Items);
        Assert.Equal(orderId, row.OrderId);
        Assert.Equal(800, row.Amount);
        Assert.Contains("1000", row.Description);

        await using (var db = pg.NewDb())
        {
            var income = await db.Incomes.SingleAsync(i => i.OrderId == orderId);
            Assert.Equal(3m, income.WorkHours);
            Assert.Equal(100m, income.LaborHourRate);
        }

        await Assert.ThrowsAsync<BadRequestException>(() => WithIncomes(b, s => s.UpdateIncomeAsync(row.Id,
            new SaveIncomeDto(row.Date, IncomeCategory.Sales, "x", 1))));
        await Assert.ThrowsAsync<BadRequestException>(() => WithIncomes(b, async s => { await s.DeleteIncomeAsync(row.Id); return 0; }));

        await using (var db = pg.NewDb())
        {
            await new OrdersService(db, PostgresFixture.TenantFor(b)).UpdateStatusAsync(orderId, OrderStatus.Ready);
        }

        Assert.Empty((await WithFinances(b, s => s.GetFinancesAsync(null, null, null, null))).Items);
    }

    [Fact]
    public async Task Export_IsExcelFriendlyCsv_AndDefusesFormulas()
    {
        var b = pg.BusinessE;
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2021, 6, 1), 70, "=HYPERLINK(\"x\")", receipt: true)));
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2021, 6, 2), 30, "אריזות, ושקיות")));

        var bytes = await WithFinances(b, s => s.ExportCsvAsync(D(2021, 6, 1), D(2021, 6, 30), "expense", null));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("תאריך,סוג,קטגוריה,תיאור,סכום,קבלה", text);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        Assert.Contains("\"אריזות, ושקיות\",-30.00,אין", text);
    }
}
