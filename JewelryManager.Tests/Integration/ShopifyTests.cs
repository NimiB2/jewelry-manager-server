using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Finances;
using JewelryManager.Api.Features.Invoices;
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
            lines.Sum(l => l.UnitPrice * l.Quantity) - discount, discount, "ILS", null, lines, "paid");

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

    // ── Later changes from the store ─────────────────────────────────────────

    private async Task<OrderResponse> ImportKnownAsync(string externalId, string storeProductId)
    {
        await WithOrders(s => s.ImportStoreOrderAsync(StoreOrder(externalId, 0, Line("Ring " + externalId, storeProductId, 100))));
        return (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#" + externalId);
    }

    [Fact]
    public async Task ACancelledStoreOrder_IsFlagged_NeverTouchesTheBooks_AndCannotBeApprovedUntilDismissed()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "C" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);

        // Already approved and on the books: the cancellation is a warning, the income stays.
        var approvedOrder = await ImportKnownAsync("31" + u, storeId);
        await WithOrders(s => s.ApproveAsync(approvedOrder.Id));
        Assert.True(await WithOrders(s => s.ApplyStoreChangeAsync("31" + u, StoreOrderAlert.Cancelled)));

        var flagged = await WithOrders(s => s.GetOrderAsync(approvedOrder.Id));
        Assert.Equal(StoreOrderAlert.Cancelled, flagged.StoreAlert);
        Assert.NotNull(flagged.StoreAlertAt);
        await using (var db = pg.NewDb())
            Assert.Equal(1, await db.Incomes.CountAsync(i => i.OrderId == approvedOrder.Id));

        // It is listed for the owner until the warning is dismissed.
        Assert.Contains(await WithOrders(s => s.GetAlertedOrdersAsync()), o => o.Id == approvedOrder.Id);

        // Still waiting: it cannot be approved, the owner has to decide first.
        var waiting = await ImportKnownAsync("32" + u, storeId);
        await WithOrders(s => s.ApplyStoreChangeAsync("32" + u, StoreOrderAlert.Cancelled));
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(s => s.ApproveAsync(waiting.Id)));

        var dismissed = await WithOrders(s => s.DismissStoreAlertAsync(waiting.Id));
        Assert.Equal(StoreOrderAlert.None, dismissed.StoreAlert);
        Assert.False((await WithOrders(s => s.ApproveAsync(waiting.Id))).IsPendingApproval);
    }

    [Fact]
    public async Task AnUpdate_FlagsOnlyARealChange_AndNeverHidesACancellation()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "U" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);

        var original = StoreOrder("41" + u, 0, Line("Ring " + u, storeId, 100));
        await WithOrders(s => s.ImportStoreOrderAsync(original));
        var order = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#41" + u);

        // The store announces an update that changes nothing the owner sees (a tag, a shipping step).
        Assert.False(await WithOrders(s => s.ApplyStoreChangeAsync("41" + u, StoreOrderAlert.Updated, original.Fingerprint())));
        Assert.Equal(StoreOrderAlert.None, (await WithOrders(s => s.GetOrderAsync(order.Id))).StoreAlert);

        // A real change: the quantity went up.
        var changed = original with { Lines = [original.Lines[0] with { Quantity = 2 }] };
        Assert.True(await WithOrders(s => s.ApplyStoreChangeAsync("41" + u, StoreOrderAlert.Updated, changed.Fingerprint())));
        Assert.Equal(StoreOrderAlert.Updated, (await WithOrders(s => s.GetOrderAsync(order.Id))).StoreAlert);

        // The same update arriving again does not raise it a second time.
        Assert.False(await WithOrders(s => s.ApplyStoreChangeAsync("41" + u, StoreOrderAlert.Updated, changed.Fingerprint())));

        // A cancellation replaces it, and a later update cannot hide the cancellation.
        await WithOrders(s => s.ApplyStoreChangeAsync("41" + u, StoreOrderAlert.Cancelled));
        var again = original with { Lines = [original.Lines[0] with { Quantity = 3 }] };
        await WithOrders(s => s.ApplyStoreChangeAsync("41" + u, StoreOrderAlert.Updated, again.Fingerprint()));
        Assert.Equal(StoreOrderAlert.Cancelled, (await WithOrders(s => s.GetOrderAsync(order.Id))).StoreAlert);
    }

    [Fact]
    public async Task ADeletion_IsFlagged_AndAnOrderWeNeverReceivedIsIgnored()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "D" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);
        var order = await ImportKnownAsync("51" + u, storeId);

        Assert.True(await WithOrders(s => s.ApplyStoreChangeAsync("51" + u, StoreOrderAlert.Deleted)));
        Assert.Equal(StoreOrderAlert.Deleted, (await WithOrders(s => s.GetOrderAsync(order.Id))).StoreAlert);

        // A change to an order that was never imported must not create one.
        Assert.False(await WithOrders(s => s.ApplyStoreChangeAsync("never-" + u, StoreOrderAlert.Cancelled)));
        await using var db = pg.NewDb();
        Assert.False(await db.Orders.AnyAsync(o => o.ExternalId == "never-" + u));
    }

    [Fact]
    public async Task Attention_CountsWaitingOrdersAndStoreWarnings_OnceEach()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "A" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);

        var before = await WithOrders(s => s.GetAttentionAsync());

        var order = await ImportKnownAsync("61" + u, storeId);
        var afterNew = await WithOrders(s => s.GetAttentionAsync());
        Assert.Equal(before.Pending + 1, afterNew.Pending);
        Assert.Equal(before.Total + 1, afterNew.Total);

        // Waiting and flagged is still one order to look at.
        await WithOrders(s => s.ApplyStoreChangeAsync("61" + u, StoreOrderAlert.Cancelled));
        var flagged = await WithOrders(s => s.GetAttentionAsync());
        Assert.Equal(before.Alerts + 1, flagged.Alerts);
        Assert.Equal(before.Total + 1, flagged.Total);

        // Rejecting it removes it from every count.
        await RunOrders(s => s.DeleteOrderAsync(order.Id));
        var done = await WithOrders(s => s.GetAttentionAsync());
        Assert.Equal(before.Total, done.Total);
        Assert.Equal(before.Alerts, done.Alerts);
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

    [Fact]
    public async Task AnUnpaidOrder_IsLeftOut_UntilTheStoreAnnouncesPayment_AndACouponNamesTheDiscount()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var domain = $"paid-{u}.myshopify.com";

        await using (var db = pg.NewDb())
        {
            var business = await db.Businesses.SingleAsync(b => b.Id == Business);
            business.ShopifySettings = $$"""{"shopDomain":"{{domain}}"}""";
            await db.SaveChangesAsync();
        }

        async Task<StoreOrderImportResult> Import(IncomingOrder order)
        {
            await using var db = pg.NewDb();
            var tenant = new CurrentUserAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
            var service = new ShopifyOrderImportService(
                db, tenant, new OrdersService(db, tenant), Options.Create(new ShopifyOptions { ShopDomain = domain, WebhookSecret = "s" }));
            return await service.ImportAsync(order);
        }

        var paid = StoreOrder("71" + u, 10, Line("Ring " + u, "P" + u, 100)) with { Discounts = [new IncomingDiscount("SPRING10", true, [])] };
        var unpaid = paid with { FinancialStatus = "pending" };

        // Created but not paid: nothing appears for the owner.
        Assert.Equal(StoreOrderImportResult.NotPaid, await Import(unpaid));
        Assert.DoesNotContain(await WithOrders(s => s.GetPendingOrdersAsync()), o => o.ExternalName == "#71" + u);

        // The store announces the payment (the same order again, now paid): it arrives, with the coupon.
        Assert.Equal(StoreOrderImportResult.Created, await Import(paid));
        var order = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#71" + u);
        Assert.Equal("קופון SPRING10", order.DiscountReason);
        Assert.Equal(90m, order.FinalAmount);

        // A discount without a coupon keeps the plain wording.
        var plain = StoreOrder("72" + u, 10, Line("Ring " + u, "P" + u, 100));
        Assert.Equal(StoreOrderImportResult.Created, await Import(plain));
        Assert.Equal("הנחה מהחנות", (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#72" + u).DiscountReason);
    }

    // ── Old orders from the store's export ───────────────────────────────────

    private static HistoricalOrder Old(string externalId, bool fulfilled, decimal discount, params HistoricalLine[] lines) =>
        new(externalId, "#" + externalId, new DateOnly(2024, 5, 3), "דנה כהן", discount,
            discount > 0 ? "קופון OLD10" : null, null, fulfilled, fulfilled ? new DateOnly(2024, 5, 6) : null, lines);

    [Fact]
    public async Task AnOldFulfilledOrder_ComesInFinished_WithIncomeAndNoTask_AndAnUnfulfilledOneStaysOpenWithATask()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var product = await AddProductAsync("מוצר " + u, 100, "Gold Ring " + u, "H" + u);

        // "Title - variant": the catalog knows the title, the variant becomes the line's note.
        var done = Old("81" + u, true, 20, new HistoricalLine("Gold Ring " + u + " - 54", null, 2, 150));
        var outcome = await WithOrders(s => s.ImportHistoricalOrderAsync(done));

        Assert.True(outcome.Created);
        Assert.Equal(1, outcome.MatchedLines);
        Assert.Empty(outcome.UnmatchedTitles);

        var order = (await WithOrders(s => s.GetOrdersAsync("all", null, null, null))).Orders.Single(o => o.ExternalName == "#81" + u);
        Assert.False(order.IsPendingApproval);
        Assert.Equal(OrderSource.Shopify, order.Source);
        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.True(order.IsCompleted);
        Assert.True(order.ReceiptSent);
        Assert.Equal(new DateOnly(2024, 5, 3), order.Date);
        Assert.Equal(300m, order.Amount);
        Assert.Equal(280m, order.FinalAmount);
        Assert.Equal("קופון OLD10", order.DiscountReason);
        Assert.Equal(product.Id, order.Items[0].ProductId);
        Assert.Equal("54", order.Items[0].Note);

        await using (var db = pg.NewDb())
        {
            var income = await db.Incomes.SingleAsync(i => i.OrderId == order.Id);
            Assert.Equal(280m, income.Amount);
            Assert.Equal(new DateOnly(2024, 5, 3), income.Date);
            Assert.Equal(0, await db.Tasks.CountAsync(t => t.OrderId == order.Id));
        }

        // Not fulfilled yet: still something to make, so it is open and has a task.
        await WithOrders(s => s.ImportHistoricalOrderAsync(Old("82" + u, false, 0, new HistoricalLine("Gold Ring " + u, null, 1, 150))));
        var open = (await WithOrders(s => s.GetOrdersAsync("all", null, null, null))).Orders.Single(o => o.ExternalName == "#82" + u);
        Assert.Equal(OrderStatus.New, open.Status);
        Assert.False(open.IsCompleted);
        await using (var db = pg.NewDb())
            Assert.Equal(1, await db.Tasks.CountAsync(t => t.OrderId == open.Id && t.IsAutomatic));
    }

    [Fact]
    public async Task AnOldOrder_IsImportedOnce_KeepsTheStoreNameOfAnItemTheCatalogLacks_AndADryRunSavesNothing()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var old = Old("91" + u, true, 0, new HistoricalLine("Mystery Earrings " + u, null, 1, 80));

        // A dry run reports the same outcome and leaves no trace.
        var dry = await WithOrders(s => s.ImportHistoricalOrderAsync(old, dryRun: true));
        Assert.True(dry.Created);
        Assert.Equal(["Mystery Earrings " + u], dry.UnmatchedTitles);
        await using (var db = pg.NewDb())
            Assert.False(await db.Orders.AnyAsync(o => o.ExternalId == "91" + u));

        Assert.True((await WithOrders(s => s.ImportHistoricalOrderAsync(old))).Created);
        Assert.False((await WithOrders(s => s.ImportHistoricalOrderAsync(old))).Created); // second time: already there

        await using var check = pg.NewDb();
        var order = await check.Orders.Include(o => o.Items).SingleAsync(o => o.ExternalId == "91" + u);
        Assert.Null(order.Items.Single().ProductId);
        Assert.Equal("Mystery Earrings " + u, order.Items.Single().Name);
        Assert.Equal(80m, order.FinalAmount);
        Assert.Equal(1, await check.Incomes.CountAsync(i => i.OrderId == order.Id));
    }

    // ── A waiting store order is not a sale ──────────────────────────────────

    private static readonly LocalInvoiceStorage Storage = new(Path.Combine(Path.GetTempPath(), "jewelry-manager-tests-invoices"));

    private async Task OpenFinancesAsync()
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(Business);
        await new FinancesService(db, tenant, new ExpensesService(db, tenant, Storage)).GetFinancesAsync(null, null, null, null);
    }

    [Fact]
    public async Task OpeningFinances_GivesAWaitingStoreOrderNoIncome_RemovesOneItWasGivenByMistake_AndApprovalStillWorks()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "F" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);

        var order = await ImportKnownAsync("A1" + u, storeId);

        // The home and finances screens load the books; that must not turn a waiting order into income.
        await OpenFinancesAsync();
        await using (var db = pg.NewDb())
            Assert.Equal(0, await db.Incomes.CountAsync(i => i.OrderId == order.Id));

        // An income that an older version gave it by mistake is taken back, and is not counted.
        await using (var db = pg.NewDb())
        {
            db.Incomes.Add(new Income
            {
                Id = Guid.NewGuid(), BusinessId = Business, OrderId = order.Id, Date = order.Date,
                Description = "stray", Amount = 100, Category = IncomeCategory.Sales, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await OpenFinancesAsync();
        await using (var db = pg.NewDb())
            Assert.Equal(0, await db.Incomes.CountAsync(i => i.OrderId == order.Id));

        // Approval makes exactly one income and one task.
        Assert.False((await WithOrders(s => s.ApproveAsync(order.Id))).IsPendingApproval);
        await using (var db = pg.NewDb())
        {
            Assert.Equal(1, await db.Incomes.CountAsync(i => i.OrderId == order.Id));
            Assert.Equal(1, await db.Tasks.CountAsync(t => t.OrderId == order.Id && t.IsAutomatic));
        }
    }

    [Fact]
    public async Task Approval_ReusesAnIncomeThatAlreadyExists_InsteadOfFailingOnTheUniqueKey()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "G" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);
        var order = await ImportKnownAsync("A2" + u, storeId);

        // The state the live database was in: the order is waiting, yet an income row already exists.
        await using (var db = pg.NewDb())
        {
            db.Incomes.Add(new Income
            {
                Id = Guid.NewGuid(), BusinessId = Business, OrderId = order.Id, Date = order.Date,
                Description = "stray", Amount = 1, Category = IncomeCategory.Sales, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await WithOrders(s => s.ApproveAsync(order.Id));

        await using var check = pg.NewDb();
        var income = await check.Incomes.SingleAsync(i => i.OrderId == order.Id);
        Assert.Equal(100m, income.Amount); // brought in line with the order
        Assert.Equal(1, await check.Tasks.CountAsync(t => t.OrderId == order.Id && t.IsAutomatic));
    }

    [Fact]
    public async Task AStoreOrder_ComesWithThePhoneAndTheMessage_ApprovalMarksTheReceiptSent_AndTheSummaryCanBeMarkedSent()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "M" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);

        var incoming = StoreOrder("B1" + u, 0, Line("Ring " + u, storeId, 450)) with { Phone = "052-1234567" };
        await WithOrders(s => s.ImportStoreOrderAsync(incoming));
        var order = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#B1" + u);

        Assert.Equal("052-1234567", order.CustomerPhone);
        Assert.Equal("972521234567", order.CustomerWhatsApp);
        Assert.Contains("שם המזמין/ה: דנה כהן", order.CustomerMessage);
        Assert.Contains("• התכשיט: מוצר " + u, order.CustomerMessage); // the catalog name, not the store's
        // She paid the catalog price (450 against a catalog price of 100): the whole amount, not a deposit.
        Assert.Contains("• סכום ששולם: 450 ₪", order.CustomerMessage);
        Assert.Null(order.CustomerNotifiedAt);
        Assert.False(order.ReceiptSent);

        // Marked as sent while the order still waits (the message goes out when she orders).
        var sent = await WithOrders(s => s.SetCustomerNotifiedAsync(order.Id, true));
        Assert.NotNull(sent.CustomerNotifiedAt);
        Assert.True(sent.IsPendingApproval);

        // The store already sent her the receipt, so approving marks it so.
        var approved = await WithOrders(s => s.ApproveAsync(order.Id));
        Assert.True(approved.ReceiptSent);
        Assert.NotNull(approved.CustomerNotifiedAt);

        Assert.Null((await WithOrders(s => s.SetCustomerNotifiedAsync(order.Id, false))).CustomerNotifiedAt);
    }

    [Fact]
    public async Task ADepositIsWordedAsOne_AndThePhoneIsKeptOnlyUntilTheSummaryIsSent()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "N" + u;
        await AddProductAsync("מוצר " + u, 2200, "Ring " + u, storeId);

        // 500 paid on a piece that costs 2,200: a deposit.
        var incoming = StoreOrder("B2" + u, 0, Line("Ring " + u, storeId, 500)) with { Phone = "050-1112222" };
        await WithOrders(s => s.ImportStoreOrderAsync(incoming));
        var order = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#B2" + u);

        Assert.Contains("• מקדמה ששולמה: 500 ₪", order.CustomerMessage);
        Assert.Equal("972501112222", order.CustomerWhatsApp);

        // Once it is marked sent the number is gone from the system.
        var sent = await WithOrders(s => s.SetCustomerNotifiedAsync(order.Id, true));
        Assert.Null(sent.CustomerPhone);
        Assert.Null(sent.CustomerWhatsApp);
        await using (var db = pg.NewDb())
            Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).CustomerPhone);
    }

    [Fact]
    public async Task APhoneGoesWithARejectedOrder_AndAnOldUnsentOneIsErasedWhenTheNextOrderArrives()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var storeId = "P" + u;
        await AddProductAsync("מוצר " + u, 100, "Ring " + u, storeId);

        // Rejecting an order removes its number too.
        await WithOrders(s => s.ImportStoreOrderAsync(
            StoreOrder("B3" + u, 0, Line("Ring " + u, storeId, 100)) with { Phone = "050-0000003" }));
        var rejected = (await WithOrders(s => s.GetPendingOrdersAsync())).Single(o => o.ExternalName == "#B3" + u);
        await RunOrders(s => s.DeleteOrderAsync(rejected.Id));
        await using (var db = pg.NewDb())
            Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == rejected.Id)).CustomerPhone);

        // An order from long ago that was never marked sent keeps no number once a new order arrives.
        await WithOrders(s => s.ImportStoreOrderAsync(
            StoreOrder("B4" + u, 0, Line("Ring " + u, storeId, 100)) with { Phone = "050-0000004", Date = new DateOnly(2020, 1, 1) }));
        await WithOrders(s => s.ImportStoreOrderAsync(StoreOrder("B5" + u, 0, Line("Ring " + u, storeId, 100))));

        await using var check = pg.NewDb();
        Assert.Null((await check.Orders.AsNoTracking().SingleAsync(o => o.ExternalId == "B4" + u)).CustomerPhone);
    }

    [Fact]
    public async Task AManualOrder_HasNoCustomerMessage_AndKeepsItsReceiptMarkAsIs()
    {
        await EnsureSetupAsync();
        var u = Unique();
        var product = await AddProductAsync("מוצר ידני " + u, 100);

        var created = await WithOrders(s => s.CreateOrderAsync(new SaveOrderDto(
            "לקוחה " + u, new DateOnly(2026, 10, 12), null,
            [new OrderItemInputDto(null, product.Id, 1, null, null)], null)));

        Assert.Null(created.CustomerMessage);
        Assert.False(created.ReceiptSent);
    }
}
