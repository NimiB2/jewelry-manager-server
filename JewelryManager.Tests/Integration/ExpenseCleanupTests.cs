using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Invoices;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>The bulk delete of expenses that have no invoice.</summary>
[Collection("postgres")]
public class ExpenseCleanupTests(PostgresFixture pg)
{
    private static readonly LocalInvoiceStorage Storage = new(Path.Combine(Path.GetTempPath(), "jewelry-manager-tests-invoices"));

    private async Task<T> With<T>(Func<ExpensesService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new ExpensesService(db, PostgresFixture.TenantFor(pg.BusinessN), Storage));
    }

    private Task<ExpenseResponse> Add(string description, decimal amount, int? repeat = null) =>
        With(s => s.CreateExpenseAsync(new CreateExpenseDto(new DateOnly(2040, 5, 10), ExpenseCategory.Variable, description, amount, repeat)));

    [Fact]
    public async Task OnlyExpensesWithoutAnInvoice_AreListedAndDeleted_RecurringOnlyWhenAsked_AndAStaleCountDeletesNothing()
    {
        var plain = await Add("מזומן", 30);
        var recurring = await Add("חוזרת", 50, repeat: 1);
        var withTick = await Add("עם חשבונית", 70);
        await using (var db = pg.NewDb())
        {
            await db.Expenses.Where(e => e.Id == withTick.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.HasReceipt, true));
        }

        var list = await With(s => s.GetWithoutReceiptAsync(null, null, includeRecurring: false));
        Assert.Equal([plain.Id], list.Items.Select(i => i.Id));
        Assert.Equal(30, list.Total);

        var withRecurring = await With(s => s.GetWithoutReceiptAsync(null, null, includeRecurring: true));
        Assert.Equal(2, withRecurring.Items.Count);

        // The books changed after she looked: nothing is deleted.
        await Assert.ThrowsAsync<BadRequestException>(() =>
            With(s => s.DeleteWithoutReceiptAsync(new DeleteWithoutReceiptDto(null, null, false, ExpectedCount: 5))));
        Assert.Equal(1, (await With(s => s.GetWithoutReceiptAsync(null, null, false))).Items.Count);

        Assert.Equal(1, await With(s => s.DeleteWithoutReceiptAsync(new DeleteWithoutReceiptDto(null, null, false, 1))));

        // The recurring one and the one with an invoice are still there.
        await using var check = pg.NewDb();
        var left = await check.Expenses.Where(e => e.BusinessId == pg.BusinessN).Select(e => e.Id).ToListAsync();
        Assert.Contains(recurring.Id, left);
        Assert.Contains(withTick.Id, left);
        Assert.DoesNotContain(plain.Id, left);
    }
}
