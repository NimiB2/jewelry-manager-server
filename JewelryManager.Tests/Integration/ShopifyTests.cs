using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Orders;
using JewelryManager.Api.Features.Orders.Dtos;
using JewelryManager.Api.Features.Shopify;
using JewelryManager.Api.Features.Shopify.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Tests.Integration;

/// <summary>The store connection against real PostgreSQL: product import, order import, linking and approval.</summary>
[Collection("postgres")]
public class ShopifyTests(PostgresFixture pg)
{
    private Guid Business => pg.BusinessI;

    private sealed class FakeCatalog(params StoreProduct[] products) : IShopifyCatalogClient
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<StoreProduct>> GetActiveProductsAsync() => Task.FromResult<IReadOnlyList<StoreProduct>>(products);
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private static StoreProduct Store(string id, string title, params decimal[] prices) =>
        new(id, title, prices.Select((p, i) => new StoreVariant($"Option {i + 1}", p, null)).ToList());

    private static IncomingLine Line(string title, string? storeProductId, decimal price = 100, int quantity = 1) =>
        new(Unique(), title, null, null, quantity, price, storeProductId);

    private static IncomingOrder StoreOrder(string externalId, decimal discount, params IncomingLine[] lines) =>
        new(externalId, "#" + externalId, new DateOnly(2026, 10, 10), "דנה כהן",
            lines.Sum(l => l.UnitPrice * l.Quantity) - discount, discount, "ILS", null, lines);

    private async Task EnsureSetupAsync()
    {
        await using var db = pg.NewDb();
        var now = DateTime.UtcNow;
        if (!await db.Settings.AnyAsync(s => s.BusinessId == Business))
            db.Settings.Add(new Settings { Id = Guid.NewGuid(), BusinessId = Business, CreatedAt = now, UpdatedAt = now });
        if (!await db.Collections.AnyAsync(c => c.BusinessId == Business && c.Key == "general"))
            db.Collections.Add(new Collection
            {
                Id = Guid.NewGuid(), BusinessId = Business, Name = "כללי", Key = "general",
                IsPermanent = true, CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();
    }

    private async Task<Product> AddProductAsync(string name, decimal price = 0, string? shopifyName = null, string? shopifyId = null)
    {
        await using var db = pg.NewDb();
        var now = DateTime.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(), BusinessId = Business, Name = name, Type = "טבעת", Material = "כסף", SitePrice = price,
            ShopifyName = shopifyName, ShopifyProductId = shopifyId, CreatedAt = now, UpdatedAt = now,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private async Task<Product> ReloadAsync(Guid id)
    {
        await using var db = pg.NewDb();
        return await db.Products.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task<T> WithImporter<T>(StoreProduct[] store, Func<ShopifyImportService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var service = new ShopifyImportService(
            db, PostgresFixture.TenantFor(Business), new FakeCatalog(store), Options.Create(new ShopifyOptions { ShopDomain = "x", AccessToken = "t" }));
        return await action(service);
    }

    private async Task RunImporter(StoreProduct[] store, Func<ShopifyImportService, Task> action) =>
        await WithImporter<bool>(store, async s =>
        {
            await action(s);
            return true;
        });

    private async Task RunOrders(Func<OrdersService, Task> action) =>
        await WithOrders<bool>(async s =>
        {
            await action(s);
            return true;
        });

    private async Task<T> WithOrders<T>(Func<OrdersService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new OrdersService(db, PostgresFixture.TenantFor(Business)));
    }

    // ── Product import ───────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ClassifiesEveryStoreProduct()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var linked = await AddProductAsync("טבעת מקושרת " + u, 100, "Linked " + u, "L" + u);
        var named = await AddProductAsync("Gold Ring " + u, 0);
        var pricey = await AddProductAsync("תליון " + u, 777.77m);

        var store = new[]
        {
            Store("L" + u, "Linked " + u, 100),
            Store("N" + u, "gold ring  " + u, 500),
            Store("P" + u, "Pendant " + u, 700, 777.77m),
            Store("X" + u, "Nothing like it " + u, 12345.67m),
        };

        var preview = await WithImporter(store, s => s.PreviewAsync());
        StoreProductPreviewDto Find(string id) => preview.Single(p => p.ExternalId == id);

        Assert.Equal("linked", Find("L" + u).Status);
        Assert.Equal(linked.Id, Find("L" + u).LinkedProductId);

        // Same name apart from case and spacing: one clear match.
        Assert.Equal("match", Find("N" + u).Status);
        Assert.Equal(named.Id, Find("N" + u).Candidates.Single().ProductId);
        Assert.Equal("name", Find("N" + u).Candidates.Single().Reason);

        // Only the price agrees: a suggestion, not a match; every variant price is shown.
        var pendant = Find("P" + u);
        Assert.Equal("candidates", pendant.Status);
        Assert.Equal(pricey.Id, pendant.Candidates.Single().ProductId);
        Assert.Equal("price", pendant.Candidates.Single().Reason);
        Assert.Equal([700m, 777.77m], pendant.Variants.Select(v => v.Price));
        Assert.Equal(700m, pendant.LowestPrice);

        Assert.Equal("none", Find("X" + u).Status);
    }

    [Fact]
    public async Task Link_WarnsBeforeReplacing_ThenMovesTheLink()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var first = await AddProductAsync("ראשון " + u);
        var second = await AddProductAsync("שני " + u);
        var store = new[] { Store("A" + u, "Alpha " + u, 100), Store("B" + u, "Beta " + u, 200) };

        await RunImporter(store, s => s.LinkAsync(new LinkStoreProductDto("A" + u, first.Id)));
        var linked = await ReloadAsync(first.Id);
        Assert.Equal("A" + u, linked.ShopifyProductId);
        Assert.Equal("Alpha " + u, linked.ShopifyName);
        Assert.Contains("Option 1", linked.ShopifyVariants);

        // The product is already tied to another store product.
        var productClash = await Assert.ThrowsAsync<ConflictException>(() =>
            RunImporter(store, s => s.LinkAsync(new LinkStoreProductDto("B" + u, first.Id))));
        Assert.Equal("PRODUCT_ALREADY_LINKED", productClash.Code);

        // The store product is already tied to another catalog product.
        var storeClash = await Assert.ThrowsAsync<ConflictException>(() =>
            RunImporter(store, s => s.LinkAsync(new LinkStoreProductDto("A" + u, second.Id))));
        Assert.Equal("STORE_PRODUCT_ALREADY_LINKED", storeClash.Code);

        // Confirmed: the old owner lets go and the second product takes the store product.
        await RunImporter(store, s => s.LinkAsync(new LinkStoreProductDto("A" + u, second.Id, Replace: true)));
        Assert.Null((await ReloadAsync(first.Id)).ShopifyProductId);
        Assert.Null((await ReloadAsync(first.Id)).ShopifyName);
        Assert.Equal("A" + u, (await ReloadAsync(second.Id)).ShopifyProductId);
    }

    [Fact]
    public async Task Create_AddsOnlyWhatIsMissing_WithNamePriceAndVariants()
    {
        await EnsureSetupAsync();
        var u = Unique();
        await AddProductAsync("קיים " + u, 0, "Existing " + u, "E" + u);
        var store = new[] { Store("E" + u, "Existing " + u, 50), Store("N" + u, "Brand New " + u, 180, 150, 210) };

        var result = await WithImporter(store, s => s.CreateAsync(new CreateFromStoreDto(["E" + u, "N" + u])));

        Assert.Equal(1, result.Created);
        Assert.Equal(["Existing " + u], result.Skipped);

        await using var db = pg.NewDb();
        var created = await db.Products.Include(p => p.Collections)
            .SingleAsync(p => p.BusinessId == Business && p.ShopifyProductId == "N" + u);

        Assert.Equal("Brand New " + u, created.Name);
        Assert.Equal("Brand New " + u, created.ShopifyName);
        Assert.Equal(150m, created.SitePrice); // the lowest of the variant prices
        Assert.True(created.NeedsDetails);
        Assert.Single(created.Collections);
        Assert.Contains("210", created.ShopifyVariants);

        // Running it again adds nothing: no duplicates.
        var again = await WithImporter(store, s => s.CreateAsync(new CreateFromStoreDto(["N" + u])));
        Assert.Equal(0, again.Created);
    }

    // ── Order import ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportedOrder_MatchesById_ThenByName_LearnsTheId_IsIdempotent_AndStaysOutOfTheList()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var byId = await AddProductAsync("לפי מזהה " + u, 100, null, "ID" + u);
        var byName = await AddProductAsync("לפי שם " + u, 100, "Named " + u, null);
        var externalId = "9" + u;

        var order = StoreOrder(externalId, 20,
            Line("Anything the store calls it", "ID" + u, 100),
            Line("named " + u, "NEW" + u, 100, 2),
            Line("Unknown piece " + u, "UNK" + u, 50));

        Assert.True(await WithOrders(s => s.ImportStoreOrderAsync(order)));
        Assert.False(await WithOrders(s => s.ImportStoreOrderAsync(order))); // the store retried

        var pending = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == order.DisplayName);

        Assert.True(pending.IsPendingApproval);
        Assert.Equal(OrderSource.Shopify, pending.Source);
        Assert.Equal(350m, pending.Amount);
        Assert.Equal(330m, pending.FinalAmount); // the store's discount is kept
        Assert.True(pending.HasDiscount);

        Assert.Equal(byId.Id, pending.Items[0].ProductId);
        Assert.Equal(byName.Id, pending.Items[1].ProductId);
        Assert.False(pending.Items[1].NeedsProduct);
        Assert.Null(pending.Items[2].ProductId);
        Assert.True(pending.Items[2].NeedsProduct);
        Assert.Equal("Unknown piece " + u, pending.Items[2].Name);

        // The name match taught the store id for the next order.
        Assert.Equal("NEW" + u, (await ReloadAsync(byName.Id)).ShopifyProductId);

        // Pending orders are not in the regular list, and cannot be worked on before approval.
        var list = await WithOrders(s => s.GetOrdersAsync("all", null, null, null));
        Assert.DoesNotContain(list.Orders, o => o.Id == pending.Id);
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(s => s.UpdateStatusAsync(pending.Id, OrderStatus.InProgress)));
    }

    [Fact]
    public async Task LinkingALine_TeachesTheMatch_TiesTheOtherPendingOrders_WarnsOnClashes_AndApprovalNeedsEveryLine()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var product = await AddProductAsync("מוצר " + u, 100);
        var storeId = "S" + u;

        await WithOrders(s => s.ImportStoreOrderAsync(StoreOrder("11" + u, 0, Line("Some Ring " + u, storeId, 150))));
        await WithOrders(s => s.ImportStoreOrderAsync(StoreOrder("12" + u, 0, Line("Some Ring " + u, storeId, 150))));

        async Task<OrderResponse> Pending(string externalId) =>
            (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#" + externalId && o.Items.Any(i => i.ExternalProductId == storeId));

        var first = await Pending("11" + u);
        var second = await Pending("12" + u);

        // Not every line is tied yet.
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(s => s.ApproveAsync(first.Id)));

        var linked = await WithOrders(s => s.LinkLineAsync(first.Id, first.Items[0].Id, product.Id, replace: false));
        Assert.Equal(product.Id, linked.Items[0].ProductId);
        Assert.Equal(150m, linked.Items[0].UnitPrice); // the price the customer paid stays

        var taught = await ReloadAsync(product.Id);
        Assert.Equal(storeId, taught.ShopifyProductId);
        Assert.Equal("Some Ring " + u, taught.ShopifyName);

        // The other pending order with the same store product is tied at once.
        Assert.Equal(product.Id, (await Pending("12" + u)).Items[0].ProductId);

        // Approving makes it a real order: income and task like a manual one.
        var approved = await WithOrders(s => s.ApproveAsync(first.Id));
        Assert.False(approved.IsPendingApproval);
        await using (var db = pg.NewDb())
        {
            Assert.Equal(1, await db.Incomes.CountAsync(i => i.OrderId == first.Id));
            Assert.Equal(1, await db.Tasks.CountAsync(t => t.OrderId == first.Id && t.IsAutomatic));
            Assert.Equal(0, await db.Incomes.CountAsync(i => i.OrderId == second.Id)); // still waiting
        }
        var list = await WithOrders(s => s.GetOrdersAsync("all", null, null, null));
        Assert.Contains(list.Orders, o => o.Id == first.Id);

        // A product that is already tied to a different store product raises a warning first.
        var other = "T" + u;
        await WithOrders(s => s.ImportStoreOrderAsync(StoreOrder("13" + u, 0, Line("Another " + u, other, 80))));
        var third = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.Items.Any(i => i.ExternalProductId == other));

        var clash = await Assert.ThrowsAsync<ConflictException>(() =>
            WithOrders(s => s.LinkLineAsync(third.Id, third.Items[0].Id, product.Id, replace: false)));
        Assert.Equal("PRODUCT_ALREADY_LINKED", clash.Code);
        Assert.Equal(storeId, (await ReloadAsync(product.Id)).ShopifyProductId); // untouched by the refused attempt

        // Confirmed: the product now stands for the new store product.
        await WithOrders(s => s.LinkLineAsync(third.Id, third.Items[0].Id, product.Id, replace: true));
        Assert.Equal(other, (await ReloadAsync(product.Id)).ShopifyProductId);
    }

    [Fact]
    public async Task ARejectedOrder_DisappearsAndNeverTouchesTheBooks()
    {
        await EnsureSetupAsync();
        var u = Unique();
        await WithOrders(s => s.ImportStoreOrderAsync(StoreOrder("21" + u, 0, Line("Nobody knows " + u, "Z" + u, 40))));
        var order = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.Items.Any(i => i.ExternalProductId == "Z" + u));

        await RunOrders(s => s.DeleteOrderAsync(order.Id));

        Assert.DoesNotContain(await WithOrders(s => s.GetPendingOrdersAsync()), o => o.Id == order.Id);
        await using var db = pg.NewDb();
        Assert.Equal(0, await db.Incomes.CountAsync(i => i.OrderId == order.Id));
    }

    // ── Webhook: which business ──────────────────────────────────────────────

    [Fact]
    public async Task AWebhookOrder_GoesToTheBusinessThatNamesTheStore_AndNoOtherBusinessSeesIt()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var domain = $"shop-{u}.myshopify.com";

        await using (var db = pg.NewDb())
        {
            var business = await db.Businesses.SingleAsync(b => b.Id == Business);
            business.ShopifySettings = $$"""{"shopDomain":"{{domain}}"}""";
            await db.SaveChangesAsync();
        }

        async Task<StoreOrderImportResult> Import(string shop, IncomingOrder order)
        {
            await using var db = pg.NewDb();
            var http = new DefaultHttpContext();
            var tenant = new CurrentUserAccessor(new HttpContextAccessor { HttpContext = http }); // no signed-in user
            var service = new ShopifyOrderImportService(
                db, tenant, new OrdersService(db, tenant), Options.Create(new ShopifyOptions { ShopDomain = shop, WebhookSecret = "s" }));
            return await service.ImportAsync(order);
        }

        var incoming = StoreOrder("31" + u, 0, Line("Via webhook " + u, "W" + u, 60));

        Assert.Equal(StoreOrderImportResult.Created, await Import(domain, incoming));
        Assert.Equal(StoreOrderImportResult.Duplicate, await Import(domain, incoming));
        Assert.Equal(StoreOrderImportResult.NoBusiness, await Import($"unknown-{u}.myshopify.com", StoreOrder("32" + u, 0, Line("x", null))));

        Assert.Contains(await WithOrders(s => s.GetPendingOrdersAsync()), o => o.Items.Any(i => i.ExternalProductId == "W" + u));

        // Another business sees nothing of it.
        await using var other = pg.NewDb();
        var otherOrders = await new OrdersService(other, PostgresFixture.TenantFor(pg.BusinessJ)).GetPendingOrdersAsync();
        Assert.DoesNotContain(otherOrders, o => o.Items.Any(i => i.ExternalProductId == "W" + u));
    }
}
