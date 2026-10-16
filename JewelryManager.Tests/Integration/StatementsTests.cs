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

        var again = await Upload(StatementSource.Card, "again.csv", CardCsv);
        Assert.True(again.FileWasAlreadyUploaded);
        Assert.Equal(0, again.Added);

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

    [Fact]
    public async Task ClearOpenLines_KeepsLinesTiedToExpenses_AndTheFileCanBeReadAgain()
    {
        var csv = "תאריך חיוב,תאריך העסקה,בית העסק,סכום העסקה,סכום החיוב\n" +
                  "02/05/2035,28/04/2035,חנות א,1000.00,250.00\n" +
                  "02/05/2035,29/04/2035,חנות ב,40.00,40.00\n";
        await Upload(StatementSource.Card, "card5.csv", csv);

        // The billed amount (this month's part of a payment plan) is the amount, the transaction date is the date.
        var lines = await With(s => s.GetLinesAsync("2035-04", null));
        Assert.Equal([250m, 40m], lines.OrderBy(l => l.Date).Select(l => l.Amount));
        Assert.Equal(["חנות א", "חנות ב"], lines.OrderBy(l => l.Date).Select(l => l.Description));

        await With(s => s.CreateExpenseAsync(lines.Single(l => l.Amount == 40).Id, new CreateExpenseFromLineDto()));

        var deleted = await With(s => s.ClearOpenLinesAsync("2035-04"));
        Assert.Equal(1, deleted);

        var again = await Upload(StatementSource.Card, "card5.csv", csv);
        Assert.True(again.FileWasAlreadyUploaded);
        Assert.Equal(1, again.Added);
        Assert.Equal(2, (await With(s => s.GetLinesAsync("2035-04", null))).Count);
    }

    [Fact]
    public async Task AMonthlyExpenseEnteredOnAnotherDay_IsMatchedToItsBankLine_NotDuplicated()
    {
        // Entered on the 15th as the supplier's monthly fee; the bank charges it on the 27th under a longer name.
        await using (var db = pg.NewDb())
        {
            await new ExpensesService(db, PostgresFixture.TenantFor(pg.BusinessM), Storage).CreateExpenseAsync(
                new CreateExpenseDto(new DateOnly(2036, 9, 15), ExpenseCategory.Fixed, "רואה חשבון", 354, null, Supplier: "גבריאל לוי"));
        }

        var result = await Upload(StatementSource.Bank, "bank6.csv",
            "תאריך,סוג תנועה,חובה,זכות\n27/09/2036,גבריאל לוי רואי חשבון,354.00,\n28/09/2036,משהו אחר,354.00,\n");

        Assert.Equal(1, result.AutoMatched);
        Assert.Equal(1, result.Pending);

        var lines = await With(s => s.GetLinesAsync("2036-09", null));
        Assert.Equal(StatementLineStatus.Matched, lines.Single(l => l.Description.StartsWith("גבריאל")).Status);
    }

    [Fact]
    public async Task AKnownPayee_GetsItsDetailsAndGroupFromBefore_AndCanBeAddedInOneStep()
    {
        await using (var db = pg.NewDb())
        {
            db.ExpenseTypes.Add(new ExpenseType { Id = Guid.NewGuid(), BusinessId = pg.BusinessM, Name = "חשבונות", GroupName = "הוצאות בית" });
            await db.SaveChangesAsync();
        }

        await Upload(StatementSource.Bank, "bank7a.csv", "תאריך,סוג תנועה,חובה,זכות\n05/10/2037,חברת החשמל,200.00,\n");
        var first = (await With(s => s.GetLinesAsync("2037-10", null))).Single();
        Assert.Null(first.Learned);

        // The category alone decides the group.
        var created = await With(s => s.CreateExpenseAsync(first.Id, new CreateExpenseFromLineDto("חשמל", "חשבונות", null, null)));
        Assert.Equal("הוצאות בית", created.GroupName);

        await Upload(StatementSource.Bank, "bank7b.csv", "תאריך,סוג תנועה,חובה,זכות\n05/11/2037,חברת החשמל,200.00,\n05/11/2037,חנות לא מוכרת,10.00,\n");
        var lines = await With(s => s.GetLinesAsync("2037-11", StatementLineStatus.Pending));
        var known = lines.Single(l => l.Description == "חברת החשמל");

        Assert.Equal("חשבונות", known.Learned?.TypeName);
        Assert.Equal("חשמל", known.Learned?.Description);
        Assert.True(known.LooksRecurring);
        Assert.Null(lines.Single(l => l.Description == "חנות לא מוכרת").Learned);

        Assert.Equal(1, await With(s => s.ApplyKnownAsync("2037-11")));
        Assert.Single(await With(s => s.GetLinesAsync("2037-11", StatementLineStatus.Pending)));
    }

    [Fact]
    public async Task CreateExpense_CanMakeItARecurringMonthlyExpense()
    {
        await Upload(StatementSource.Bank, "bank8.csv", "תאריך,סוג תנועה,חובה,זכות\n03/06/2038,דמי ניהול,45.00,\n");
        var line = (await With(s => s.GetLinesAsync("2038-06", null))).Single();

        var expense = await With(s => s.CreateExpenseAsync(line.Id, new CreateExpenseFromLineDto(RepeatEveryMonths: 1)));

        Assert.Equal(1, expense.RepeatEveryMonths);
        Assert.NotNull(expense.SeriesId);
    }
}
