using System.Text;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Invoices;
using JewelryManager.Api.Features.Statements;
using JewelryManager.Api.Features.Statements.Dtos;

namespace JewelryManager.Tests.Integration;

/// <summary>Statement reconciliation against real PostgreSQL, with made-up statements.</summary>
[Collection("postgres")]
public class StatementsTests(PostgresFixture pg)
{
    private static readonly LocalInvoiceStorage Storage = new(Path.Combine(Path.GetTempPath(), "jewelry-manager-tests-invoices"));

    private async Task<T> With<T>(Func<StatementsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(pg.BusinessM);
        return await action(new StatementsService(db, tenant, new StatementFileParser(), new ExpensesService(db, tenant, Storage)));
    }

    private Task With(Func<StatementsService, Task> action) => With(async s => { await action(s); return 0; });

    private Task<UploadResultResponse> Upload(StatementSource source, string name, string csv) =>
        With(s => s.UploadAsync(source, name, csv.Length, new MemoryStream(Encoding.UTF8.GetBytes(csv))));

    private async Task<ExpenseResponse> NewExpense(DateOnly date, decimal amount, string description)
    {
        await using var db = pg.NewDb();
        return await new ExpensesService(db, PostgresFixture.TenantFor(pg.BusinessM), Storage)
            .CreateExpenseAsync(new CreateExpenseDto(date, ExpenseCategory.Variable, description, amount, null));
    }

    private const string CardCsv =
        "תאריך עסקה,שם בית עסק,סכום חיוב\n" +
        "03/01/2031,ספק חוטים,120.50\n" +
        "05/01/2031,קפה,18\n" +
        "05/01/2031,קפה,18\n" +
        "09/01/2031,החזר,-30\n";

    [Fact]
    public async Task Upload_ReadsRows_RejectsTheSameFile_AndSkipsRowsFromAnOverlappingFile()
    {
        var first = await Upload(StatementSource.Card, "card1.csv", CardCsv);
        Assert.Equal(4, first.Added);
        Assert.Equal(1, first.AutoIgnored); // the refund is money in
        Assert.Equal(3, first.Pending);     // two identical coffees are two real rows

        await Assert.ThrowsAsync<BadRequestException>(() => Upload(StatementSource.Card, "again.csv", CardCsv));

        var overlap = await Upload(StatementSource.Card, "card2.csv", CardCsv + "20/01/2031,חדש,40\n");
        Assert.Equal(1, overlap.Added);
        Assert.Equal(4, overlap.AlreadyKnown);
    }

    [Fact]
    public async Task BankSettlement_IsNotAnExpense_AndNeedsItsCardStatement_BeforeTheMonthCloses()
    {
        var bank = "תאריך,תיאור,חובה,זכות\n" +
                   "10/02/2032,ויזה כאל חיוב,100.00,\n" +
                   "11/02/2032,חשמל,250.00,\n";
        var uploaded = await Upload(StatementSource.Bank, "bank.csv", bank);
        Assert.Equal(1, uploaded.CardSettlements);
        Assert.Equal(1, uploaded.Pending);

        var check = await With(s => s.CheckMonthAsync("2032-02"));
        Assert.Single(check.SettlementsWithoutCardStatement);
        Assert.False(check.CanClose);

        // The card statement whose total equals the settlement covers it.
        await Upload(StatementSource.Card, "card.csv", "תאריך עסקה,שם בית עסק,סכום חיוב\n02/02/2032,חנות,60\n03/02/2032,חנות,40\n");
        check = await With(s => s.CheckMonthAsync("2032-02"));
        Assert.Empty(check.SettlementsWithoutCardStatement);
        Assert.Equal(3, check.PendingCount); // the electricity bill and the two card purchases

        var pendingLines = await With(s => s.GetLinesAsync("2032-02", StatementLineStatus.Pending));
        var line = pendingLines[0];
        foreach (var p in pendingLines) await With(s => s.IgnoreAsync(p.Id));
        await With(s => s.CloseMonthAsync("2032-02"));

        await Assert.ThrowsAsync<BadRequestException>(() => With(s => s.ResetAsync(line.Id)));
        await With(s => s.ReopenMonthAsync("2032-02"));
    }

    [Fact]
    public async Task Match_SuggestsTheSameAmountNearby_BlocksADoubleLink_AndADeletedExpenseFreesTheLine()
    {
        var csv = "תאריך,תיאור,חובה,זכות\n12/03/2033,ספק אבנים,77.00,\n13/03/2033,ספק אחר,77.00,\n";
        await Upload(StatementSource.Bank, "bank3.csv", csv);
        var mine = await NewExpense(new DateOnly(2033, 3, 11), 77, "אבנים");

        var lines = await With(s => s.GetLinesAsync("2033-03", StatementLineStatus.Pending));
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(mine.Id, Assert.Single(l.Suggestions).ExpenseId));

        await With(s => s.MatchAsync(lines[0].Id, mine.Id));
        await Assert.ThrowsAsync<BadRequestException>(() => With(s => s.MatchAsync(lines[1].Id, mine.Id)));

        // Once linked, the expense is no longer offered to other lines.
        var other = (await With(s => s.GetLinesAsync("2033-03", StatementLineStatus.Pending))).Single();
        Assert.Empty(other.Suggestions);

        await using var db = pg.NewDb();
        await new ExpensesService(db, PostgresFixture.TenantFor(pg.BusinessM), Storage).DeleteExpenseAsync(mine.Id, ExpenseDeleteScope.This);
        Assert.Equal(2, (await With(s => s.GetLinesAsync("2033-03", StatementLineStatus.Pending))).Count);
    }

    [Fact]
    public async Task CreateExpense_TakesTheLineValues_AndLinksIt()
    {
        await Upload(StatementSource.Bank, "bank4.csv", "תאריך,תיאור,חובה,זכות\n02/04/2034,ארנונה,300.00,\n");
        var line = (await With(s => s.GetLinesAsync("2034-04", StatementLineStatus.Pending))).Single();

        var expense = await With(s => s.CreateExpenseAsync(line.Id, new CreateExpenseFromLineDto()));
        Assert.Equal(300, expense.Amount);
        Assert.Equal("ארנונה", expense.Description);
        Assert.Equal(new DateOnly(2034, 4, 2), expense.Date);

        var done = (await With(s => s.GetLinesAsync("2034-04", null))).Single();
        Assert.Equal(StatementLineStatus.Created, done.Status);
        Assert.Equal(expense.Id, done.ExpenseId);
    }
}
