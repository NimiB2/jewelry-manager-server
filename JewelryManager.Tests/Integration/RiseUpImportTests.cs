using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Invoices;
using JewelryManager.Api.Features.RiseUp;
using JewelryManager.Api.Features.RiseUp.Dtos;
using JewelryManager.Api.Features.Statements;

namespace JewelryManager.Tests.Integration;

/// <summary>RiseUp import against real PostgreSQL, with made-up transactions in a month no other test uses.</summary>
[Collection("postgres")]
public class RiseUpImportTests(PostgresFixture pg)
{
    private const string Month = "2032-03";

    private static readonly LocalInvoiceStorage Storage = new(Path.Combine(Path.GetTempPath(), "jewelry-manager-tests-invoices"));

    private static readonly List<RiseUpTransaction> Rows =
    [
        new("ru-test-1", new DateOnly(2032, 3, 3), 120.5m, false, "Sample Yarn", "creditCard", "Card A", false, null, null),
        new("ru-test-2", new DateOnly(2032, 3, 5), 18m, false, "Sample Coffee", "creditCard", null, false, null, null),
        new("ru-test-3", new DateOnly(2032, 3, 9), 500m, true, "Sample Client", "checkingAccount", null, false, null, null),
    ];

    private async Task<RiseUpSyncResponse> Import(List<RiseUpTransaction> rows)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(pg.BusinessM);
        var service = new StatementsService(db, tenant, new StatementFileParser(), new ExpensesService(db, tenant, Storage));
        return await service.ImportRiseUpAsync(Month, rows);
    }

    [Fact]
    public async Task Import_AddsNewTransactions_AndSkipsTheSameOnesAgain()
    {
        var first = await Import(Rows);
        Assert.Equal(3, first.Added);
        Assert.Equal(1, first.AutoIgnored); // the client payment is money in

        var again = await Import(Rows);
        Assert.Equal(0, again.Added);
        Assert.Equal(3, again.AlreadyKnown);

        var later = await Import(Rows.Append(new RiseUpTransaction(
            "ru-test-4", new DateOnly(2032, 3, 12), 64m, false, "Sample Pharmacy", "checkingAccount", null, false, null, null)).ToList());
        Assert.Equal(1, later.Added);
        Assert.Equal(3, later.AlreadyKnown);
    }
}
