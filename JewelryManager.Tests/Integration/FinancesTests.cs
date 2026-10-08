using System.Text;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Finances;
using JewelryManager.Api.Features.Finances.Dtos;
using JewelryManager.Api.Features.Incomes;
using JewelryManager.Api.Features.Incomes.Dtos;
using JewelryManager.Api.Features.Invoices;
using JewelryManager.Api.Features.Orders;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>Finances against real PostgreSQL. Each test uses its own business so rows never mix.</summary>
[Collection("postgres")]
public class FinancesTests(PostgresFixture pg)
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    // Invoice files go to a temp folder, not the real storage.
    private static readonly LocalInvoiceStorage Storage = new(Path.Combine(Path.GetTempPath(), "jewelry-manager-tests-invoices"));

    private async Task<T> WithExpenses<T>(Guid business, Func<ExpensesService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new ExpensesService(db, PostgresFixture.TenantFor(business), Storage));
    }

    private async Task AttachInvoiceAsync(Guid business, Guid expenseId)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(business);
        var invoices = new InvoicesService(db, tenant, Storage, new FinancesService(db, tenant, new ExpensesService(db, tenant, Storage)));
        await invoices.AttachAsync(expenseId, "scan.pdf", 4, new MemoryStream("%PDF"u8.ToArray()));
    }

    private async Task GenerateAsync(Guid business, DateOnly today)
    {
        await using var db = pg.NewDb();
        await new ExpensesService(db, PostgresFixture.TenantFor(business), Storage).GenerateDueRecurringAsync(today);
    }

    private async Task DeleteExpenseAsync(Guid business, Guid id, ExpenseDeleteScope scope)
    {
        await using var db = pg.NewDb();
        await new ExpensesService(db, PostgresFixture.TenantFor(business), Storage).DeleteExpenseAsync(id, scope);
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
        return await action(new FinancesService(db, tenant, new ExpensesService(db, tenant, Storage)));
    }

    private static CreateExpenseDto Expense(DateOnly date, decimal amount, string description = "חומרים", int? repeat = null) =>
        new(date, ExpenseCategory.Variable, description, amount, repeat);

    [Fact]
    public async Task Summary_CoversThePeriod_AndFiltersOnlyNarrowTheList()
    {
        var b = pg.BusinessE;
        var withInvoice = await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2025, 3, 5), 100)));
        await AttachInvoiceAsync(b, withInvoice.Id);
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
        await Assert.ThrowsAsync<NotFoundException>(() => DeleteExpenseAsync(pg.BusinessF, created.Id, ExpenseDeleteScope.This));
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
        await DeleteExpenseAsync(b, first.Id, ExpenseDeleteScope.FromHere);
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
            await DeleteExpenseAsync(b, rows[3].Id, ExpenseDeleteScope.This);
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
                new UpdateExpenseDto(feb.Date, ExpenseCategory.Fixed, "ארנונה", 120, ApplyToSeries: true)));
        }

        await using (var db = pg.NewDb())
        {
            var rows = await db.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).ToListAsync();
            Assert.Equal(100, rows[0].Amount);
            Assert.All(rows.Skip(1), r => Assert.Equal(120, r.Amount));

            // Delete the whole series from March: January and February remain, the series is gone.
            var mar = rows.Single(r => r.Date == D(2022, 3, 10));
            await DeleteExpenseAsync(b, mar.Id, ExpenseDeleteScope.FromHere);
        }

        await GenerateAsync(b, D(2023, 1, 1));
        await using var final = pg.NewDb();
        Assert.Equal(2, await final.Expenses.CountAsync(e => e.BusinessId == b && e.Description == "ארנונה"));
    }

    [Fact]
    public async Task IncomeFromAnOrder_ExistsForRealOrders_IsReadOnly_AndGoesWithTheOrder()
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

        // Orders that were created before finances existed get their income on the first visit; the demo order never does.
        var list = await WithFinances(b, s => s.GetFinancesAsync(null, null, null, null));
        var row = Assert.Single(list.Items);
        Assert.Equal(orderId, row.OrderId);
        Assert.Equal(800, row.Amount);
        Assert.Equal(D(2026, 5, 1), row.Date);
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
            await new OrdersService(db, PostgresFixture.TenantFor(b)).DeleteOrderAsync(orderId);
        }

        Assert.Empty((await WithFinances(b, s => s.GetFinancesAsync(null, null, null, null))).Items);
    }

    [Fact]
    public async Task Expense_CarriesATypeAndAnOptionalOrderLink_AndTheLinkMustBelongToTheBusiness()
    {
        var b = pg.BusinessG;
        var orderId = Guid.NewGuid();
        var foreignOrderId = Guid.NewGuid();
        await using (var db = pg.NewDb())
        {
            var now = DateTime.UtcNow;
            db.Orders.AddRange(
                new Order { Id = orderId, BusinessId = b, Number = 8101, Date = D(2026, 1, 1), CreatedAt = now, UpdatedAt = now },
                new Order { Id = foreignOrderId, BusinessId = pg.BusinessH, Number = 8102, Date = D(2026, 1, 1), CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var linked = await WithExpenses(b, s => s.CreateExpenseAsync(
            new CreateExpenseDto(D(2026, 1, 5), ExpenseCategory.Variable, "אבנים", 120, null, "קניית חומר", orderId)));
        Assert.Equal("קניית חומר", linked.TypeName);
        Assert.Equal(orderId, linked.OrderId);
        Assert.Equal(8101, linked.OrderNumber);

        await Assert.ThrowsAsync<BadRequestException>(() => WithExpenses(b, s => s.CreateExpenseAsync(
            new CreateExpenseDto(D(2026, 1, 5), ExpenseCategory.Variable, "x", 1, null, null, foreignOrderId))));
        await Assert.ThrowsAsync<BadRequestException>(() => WithExpenses(b, s => s.CreateExpenseAsync(
            new CreateExpenseDto(D(2026, 1, 5), ExpenseCategory.Fixed, "x", 1, 1, null, orderId))));

        var unlinked = await WithExpenses(b, s => s.UpdateExpenseAsync(linked.Id,
            new UpdateExpenseDto(linked.Date, ExpenseCategory.Variable, "אבנים", 120, false, "קניית חומר", null)));
        Assert.Null(unlinked.OrderId);

        var item = (await WithFinances(b, s => s.GetFinancesAsync(D(2026, 1, 1), D(2026, 1, 31), "expense", null))).Items
            .Single(i => i.Id == linked.Id);
        Assert.Equal("קניית חומר", item.TypeName);
    }

    [Fact]
    public async Task Invoice_CanBeAttachedReadReplacedAndRemoved_IsPrivatePerBusiness_AndExportsToZip()
    {
        var b = pg.BusinessE;
        var expense = await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2020, 2, 3), 99, "חשבונית לבדיקה")));
        Assert.False(expense.HasReceipt);

        async Task<T> WithInvoices<T>(Guid business, Func<InvoicesService, Task<T>> action)
        {
            await using var db = pg.NewDb();
            var tenant = PostgresFixture.TenantFor(business);
            return await action(new InvoicesService(db, tenant, Storage, new FinancesService(db, tenant, new ExpensesService(db, tenant, Storage))));
        }

        await WithInvoices(b, async s => { await s.AttachAsync(expense.Id, "scan.pdf", 4, new MemoryStream("%PDF"u8.ToArray())); return 0; });
        var attached = await WithExpenses(b, s => s.GetExpenseAsync(expense.Id));
        Assert.True(attached.HasReceipt);
        Assert.Equal("scan.pdf", attached.InvoiceFileName);

        await using (var file = (await WithInvoices(b, s => s.OpenAsync(expense.Id))).Content)
        {
            using var reader = new StreamReader(file);
            Assert.Equal("%PDF", await reader.ReadToEndAsync());
        }

        // Another business can neither see nor change it.
        await Assert.ThrowsAsync<NotFoundException>(() => WithInvoices(pg.BusinessF, s => s.OpenAsync(expense.Id)));

        // Wrong types and oversized files are refused.
        await Assert.ThrowsAsync<BadRequestException>(() => WithInvoices(b, async s =>
        { await s.AttachAsync(expense.Id, "virus.exe", 4, new MemoryStream([1, 2, 3, 4])); return 0; }));
        await Assert.ThrowsAsync<BadRequestException>(() => WithInvoices(b, async s =>
        { await s.AttachAsync(expense.Id, "big.pdf", InvoicesService.MaxFileBytes + 1, new MemoryStream([1])); return 0; }));

        var zip = await WithInvoices(b, s => s.ExportZipAsync(D(2020, 2, 1), D(2020, 2, 28)));
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip)))
        {
            Assert.Contains(archive.Entries, e => e.Name.StartsWith("2020-02-03_") && e.Name.EndsWith(".pdf"));
            Assert.Contains(archive.Entries, e => e.Name == "רשימת הוצאות.csv");
        }

        await WithInvoices(b, async s => { await s.RemoveAsync(expense.Id); return 0; });
        var removed = await WithExpenses(b, s => s.GetExpenseAsync(expense.Id));
        Assert.False(removed.HasReceipt);
        Assert.Null(removed.InvoiceFileName);
    }

    [Fact]
    public async Task RecurringExpense_ShowsAsExpectedInComingMonths_ButNeverInTheTotals()
    {
        var b = pg.BusinessH;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = await WithExpenses(b, s => s.CreateExpenseAsync(Expense(today, 700, "שכירות עתידית", repeat: 1)));

        var nextMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        var nextMonthEnd = nextMonth.AddMonths(1).AddDays(-1);

        var without = await WithFinances(b, s => s.GetFinancesAsync(nextMonth, nextMonthEnd, null, null));
        Assert.Empty(without.Items);

        var with = await WithFinances(b, s => s.GetFinancesAsync(nextMonth, nextMonthEnd, null, null, includeProjected: true));
        var coming = Assert.Single(with.Items);
        Assert.True(coming.IsProjected);
        Assert.Equal(700, coming.Amount);
        Assert.Equal(first.Id, coming.Id);
        Assert.Equal(0, with.Summary.TotalExpenses);

        // Stopping the series from the current one removes the projection and keeps this month.
        await DeleteExpenseAsync(b, first.Id, ExpenseDeleteScope.After);
        var stopped = await WithFinances(b, s => s.GetFinancesAsync(nextMonth, nextMonthEnd, null, null, includeProjected: true));
        Assert.Empty(stopped.Items);
        Assert.Equal(first.Id, (await WithExpenses(b, s => s.GetExpenseAsync(first.Id))).Id);
    }

    [Fact]
    public async Task StoppingARecurringExpense_RemovesOnlyTheLaterOccurrences()
    {
        var b = pg.BusinessE;
        var first = await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2019, 1, 10), 100, "חוזרת לעצירה", repeat: 1)));
        await GenerateAsync(b, D(2019, 4, 20));

        await using (var db = pg.NewDb())
        {
            var rows = await db.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).ToListAsync();
            Assert.Equal(4, rows.Count);
            await DeleteExpenseAsync(b, rows[1].Id, ExpenseDeleteScope.After);
        }

        await GenerateAsync(b, D(2020, 1, 1));
        await using var final = pg.NewDb();
        var left = await final.Expenses.Where(e => e.SeriesId == first.SeriesId).OrderBy(e => e.Date).Select(e => e.Date).ToListAsync();
        Assert.Equal([D(2019, 1, 10), D(2019, 2, 10)], left);
    }

    [Fact]
    public async Task Supplier_IsSaved_ShownInTheList_AndSuggestedByHowOftenItWasUsed()
    {
        var b = pg.BusinessG;
        Task<ExpenseResponse> Make(string? supplier) => WithExpenses(b, s => s.CreateExpenseAsync(
            new CreateExpenseDto(D(2018, 5, 1), ExpenseCategory.Variable, "קנייה", 10, null, null, null, supplier)));

        await Make("ספק א");
        await Make("  ספק ב ");
        await Make("ספק ב");
        await Make(null);

        var suggestions = await WithExpenses(b, s => s.GetSuppliersAsync());
        Assert.Equal(["ספק ב", "ספק א"], suggestions);

        var list = await WithFinances(b, s => s.GetFinancesAsync(D(2018, 5, 1), D(2018, 5, 31), "expense", null));
        Assert.Contains(list.Items, i => i.Supplier == "ספק א");

        // Removing a supplier from her list does not touch the expenses that name it, and using it again brings it back in front.
        await using (var db = pg.NewDb())
            await db.ExpenseSuppliers.Where(x => x.BusinessId == b && x.Name == "ספק ב").ExecuteDeleteAsync();

        Assert.Equal(["ספק א"], await WithExpenses(b, s => s.GetSuppliersAsync()));
        Assert.Contains((await WithFinances(b, s => s.GetFinancesAsync(D(2018, 5, 1), D(2018, 5, 31), "expense", null))).Items, i => i.Supplier == "ספק ב");

        await Make("ספק ב");
        Assert.Equal(["ספק ב", "ספק א"], await WithExpenses(b, s => s.GetSuppliersAsync()));

        // Another business never sees these suggestions.
        Assert.Empty(await WithExpenses(pg.BusinessH, s => s.GetSuppliersAsync()));
    }

    private sealed class FakeReader(InvoiceSuggestion? result) : IInvoiceReader
    {
        public bool IsAvailable => true;

        public Task<InvoiceSuggestion?> ReadAsync(Stream content, string fileName, string contentType, IReadOnlyList<string> expenseTypes) =>
            Task.FromResult(result);
    }

    [Fact]
    public async Task InvoiceReading_IsOffWithoutAReader_AndKeepsOnlyUsableSuggestionsWithAReader()
    {
        var b = pg.BusinessG;
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(b);
        db.ExpenseTypes.Add(new ExpenseType { Id = Guid.NewGuid(), BusinessId = b, Name = "אריזה", SortOrder = 0 });
        await db.SaveChangesAsync();

        var off = await new InvoiceReadingService(db, tenant, new NullInvoiceReader())
            .ReadAsync("a.pdf", 4, new MemoryStream("%PDF"u8.ToArray()));
        Assert.False(off.Available);
        Assert.Null(off.Suggestion);

        var messy = new InvoiceSuggestion(-5, D(2026, 1, 2), "  ספק  ", null, "קטגוריה שלא קיימת");
        var on = await new InvoiceReadingService(db, tenant, new FakeReader(messy))
            .ReadAsync("a.pdf", 4, new MemoryStream("%PDF"u8.ToArray()));
        Assert.True(on.Available);
        Assert.Null(on.Suggestion!.Amount);
        Assert.Equal("ספק", on.Suggestion.Supplier);
        Assert.Null(on.Suggestion.TypeName);

        var good = new InvoiceSuggestion(120.456m, D(2026, 1, 2), "ספק", "אריזות", "אריזה");
        var ok = await new InvoiceReadingService(db, tenant, new FakeReader(good))
            .ReadAsync("a.png", 4, new MemoryStream([1, 2, 3, 4]));
        Assert.Equal(120.46m, ok.Suggestion!.Amount);
        Assert.Equal("אריזה", ok.Suggestion.TypeName);

        await Assert.ThrowsAsync<BadRequestException>(() => new InvoiceReadingService(db, tenant, new FakeReader(good))
            .ReadAsync("a.exe", 4, new MemoryStream([1])));
    }

    [Fact]
    public async Task Export_IsExcelFriendlyCsv_AndDefusesFormulas()
    {
        var b = pg.BusinessE;
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2021, 6, 1), 70, "=HYPERLINK(\"x\")")));
        await WithExpenses(b, s => s.CreateExpenseAsync(Expense(D(2021, 6, 2), 30, "אריזות, ושקיות")));

        var bytes = await WithFinances(b, s => s.ExportCsvAsync(D(2021, 6, 1), D(2021, 6, 30), "expense", null));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("תאריך,סוג,קטגוריה,סוג הוצאה,ספק,תיאור,סכום,חשבונית", text);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        Assert.Contains("\"אריזות, ושקיות\",-30.00,אין", text);
    }
}
