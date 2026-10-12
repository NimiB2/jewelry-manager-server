using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Search;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>The one search against real PostgreSQL, in businesses of their own.</summary>
[Collection("postgres")]
public class SearchTests(PostgresFixture pg)
{
    private async Task<Guid> NewBusinessAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = pg.NewDb();
        db.Businesses.Add(new Business { Id = id, Name = "Search " + id.ToString("N")[..6], CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Api.Features.Search.Dtos.SearchResponse> SearchAsync(Guid business, string? q)
    {
        await using var db = pg.NewDb();
        return await new SearchService(db, PostgresFixture.TenantFor(business)).SearchAsync(q);
    }

    [Fact]
    public async Task Finds_Orders_Products_AndExpenses_ByTheTextOrTheNumber_OfOneBusinessOnly()
    {
        var mine = await NewBusinessAsync();
        var other = await NewBusinessAsync();
        var now = DateTime.UtcNow;

        await using (var db = pg.NewDb())
        {
            Order Make(Guid business, int number, string customer, bool deleted = false) => new()
            {
                Id = Guid.NewGuid(), BusinessId = business, Number = number, Customer = customer, Date = new DateOnly(2026, 10, 12),
                Amount = 100, FinalAmount = 100, IsDeleted = deleted, CreatedAt = now, UpdatedAt = now,
            };

            db.Orders.AddRange(
                Make(mine, 1201, "רינה לוי"),
                Make(mine, 1202, "דנה כהן"),
                Make(mine, 1203, "רינה כהן", deleted: true),     // deleted: never found
                Make(other, 1201, "רינה אחרת"));                  // another business: never found

            db.Products.AddRange(
                new Product { Id = Guid.NewGuid(), BusinessId = mine, Name = "טבעת רינה", SitePrice = 900, CreatedAt = now, UpdatedAt = now },
                new Product { Id = Guid.NewGuid(), BusinessId = mine, Name = "שרשרת", ShopifyName = "Rina Necklace", SitePrice = 500, CreatedAt = now, UpdatedAt = now },
                new Product { Id = Guid.NewGuid(), BusinessId = other, Name = "טבעת רינה", SitePrice = 1, CreatedAt = now, UpdatedAt = now });

            db.Expenses.AddRange(
                new Expense { Id = Guid.NewGuid(), BusinessId = mine, Date = new DateOnly(2026, 3, 1), Description = "חומרים", Supplier = "רינה תכשיטים", Amount = 640, CreatedAt = now, UpdatedAt = now },
                new Expense { Id = Guid.NewGuid(), BusinessId = other, Date = new DateOnly(2026, 3, 1), Description = "חומרים", Supplier = "רינה תכשיטים", Amount = 1, CreatedAt = now, UpdatedAt = now });

            await db.SaveChangesAsync();
        }

        var byName = await SearchAsync(mine, "רינה");
        Assert.Equal(["רינה לוי"], byName.Orders.Select(o => o.Customer));
        Assert.Equal(["טבעת רינה"], byName.Products.Select(p => p.Name));
        Assert.Equal(["חומרים"], byName.Expenses.Select(e => e.Description));

        // The store's name for a product, and an order by its number (with or without a #).
        Assert.Equal(["שרשרת"], (await SearchAsync(mine, "rina")).Products.Select(p => p.Name));
        Assert.Equal([1202], (await SearchAsync(mine, "#1202")).Orders.Select(o => o.Number));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ר")]
    [InlineData("   ")]
    public async Task ATooShortQuery_FindsNothing(string? q)
    {
        var result = await SearchAsync(await NewBusinessAsync(), q);

        Assert.Empty(result.Orders);
        Assert.Empty(result.Products);
        Assert.Empty(result.Expenses);
    }

    [Fact]
    public async Task APercentSignIsLookedForAsItself_NotAsAWildcard()
    {
        var business = await NewBusinessAsync();
        var now = DateTime.UtcNow;

        await using (var db = pg.NewDb())
        {
            db.Products.AddRange(
                new Product { Id = Guid.NewGuid(), BusinessId = business, Name = "הנחה 50% מיוחדת", CreatedAt = now, UpdatedAt = now },
                new Product { Id = Guid.NewGuid(), BusinessId = business, Name = "טבעת רגילה", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        Assert.Equal(["הנחה 50% מיוחדת"], (await SearchAsync(business, "50%")).Products.Select(p => p.Name));
        Assert.Empty((await SearchAsync(business, "%%")).Products); // not "match everything"
    }
}
