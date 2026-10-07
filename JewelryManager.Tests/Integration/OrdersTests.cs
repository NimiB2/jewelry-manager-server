using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Orders;
using JewelryManager.Api.Features.Orders.Dtos;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>Orders against real PostgreSQL, in their own business so numbering never collides with other tests.</summary>
[Collection("postgres")]
public class OrdersTests(PostgresFixture pg)
{
    private Guid Business => pg.BusinessD;
    private static readonly DateOnly Today = new(2026, 10, 7);

    private async Task<T> WithOrders<T>(Guid business, Func<OrdersService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new OrdersService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task<T> WithProducts<T>(Func<ProductsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(Business);
        return await action(new ProductsService(db, tenant, new PricingService(db, tenant)));
    }

    // Known setup: silver 8/g with 1 fixed hour, labor 100/h, three preparation stages.
    private async Task SetupAsync(Guid? other = null)
    {
        var business = other ?? Business;
        await using (var db = pg.NewDb())
        {
            if (!await db.Settings.AnyAsync(s => s.BusinessId == business))
            {
                var now = DateTime.UtcNow;
                db.Settings.Add(new Settings { Id = Guid.NewGuid(), BusinessId = business, CreatedAt = now, UpdatedAt = now });
                await db.SaveChangesAsync();
            }
        }

        await using var settingsDb = pg.NewDb();
        await new SettingsService(settingsDb, PostgresFixture.TenantFor(business)).UpdateSettingsAsync(new UpdateSettingsDto(
            new() { ["כסף"] = new(8, 1, 1.5m) }, 100, [],
            [new("סליקה", 3, true, FeeKeys.CardFee), new("מע\"מ", 18, true, FeeKeys.Vat), new("קבועות", 17, true, FeeKeys.FixedExpenses)],
            30, ["יציקה", "שיבוץ", "ליטוש"]));
    }

    private async Task<ProductResponse> NewProductAsync(string name = "טבעת", decimal sitePrice = 1500, decimal extraHours = 0.5m) =>
        await WithProducts(s => s.CreateProductAsync(
            new SaveProductDto("טבעת", name, "כסף", 5, extraHours, sitePrice, [], [])));

    private async Task<OrderResponse> NewOrderAsync(string productName)
    {
        var product = await NewProductAsync(productName);
        return await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));
    }

    private static SaveOrderDto Order(Guid productId, int qty = 1, string? customer = "דנה", DateOnly? date = null,
        OrderDiscountDto? discount = null, decimal? price = null) =>
        new(customer, date ?? Today, null, [new OrderItemInputDto(null, productId, qty, price)], discount);

    [Fact]
    public async Task Create_SnapshotsTheProduct_AndLaterCatalogChangesDoNotTouchTheOrder()
    {
        await SetupAsync();
        var product = await NewProductAsync("טבעת קפואה");

        var order = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, qty: 2)));

        Assert.Equal(3000, order.Amount);
        Assert.Equal(3000, order.FinalAmount);
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(OrderSource.Manual, order.Source);

        // The catalog moves on: new price, new name...
        await WithProducts(s => s.UpdateSitePriceAsync(product.Id, new UpdateSitePriceDto(9999)));
        await WithProducts(s => s.UpdateProductAsync(product.Id,
            new SaveProductDto("טבעת", "שם חדש", "כסף", 5, 0.5m, 9999, [], [])));

        // ...but the order still shows what was sold.
        var read = await WithOrders(Business, s => s.GetOrderAsync(order.Id));
        var line = Assert.Single(read.Items);
        Assert.Equal("טבעת קפואה", line.Name);
        Assert.Equal(1500, line.UnitPrice);
        Assert.Equal(3000, read.Amount);
    }

    [Fact]
    public async Task Create_FreezesWorkHoursAndLaborRate()
    {
        await SetupAsync();
        var product = await NewProductAsync("שעות", extraHours: 0.5m);

        var order = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));

        // 1 fixed hour for silver + 0.5 extra.
        Assert.Equal(1.5m, Assert.Single(order.Items).WorkHours);
        await using var db = pg.NewDb();
        Assert.Equal(100, (await db.Orders.SingleAsync(o => o.Id == order.Id)).LaborHourRate);
    }

    [Fact]
    public async Task Numbers_RealOrdersFrom1000_TestOrdersFrom500_AndNeverReused()
    {
        await SetupAsync();
        var product = await NewProductAsync("מספור");

        var first = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));
        var second = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));
        var test = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, customer: "בדיקה - דנה")));

        Assert.True(first.Number >= 1000);
        Assert.Equal(first.Number + 1, second.Number);
        Assert.False(first.IsTest);
        Assert.True(test.IsTest);
        Assert.InRange(test.Number, 500, 999);

        await WithOrders(Business, async s => { await s.DeleteOrderAsync(second.Id); return 0; });
        var third = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));
        Assert.Equal(second.Number + 1, third.Number);
    }

    [Fact]
    public async Task DemoLabel_FollowsTheCustomerName_WhenRenamed_WhileTheNumberStays()
    {
        await SetupAsync();
        var product = await NewProductAsync("שינוי שם");
        var order = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, customer: "דנה")));
        Assert.False(order.IsTest);

        var lineId = order.Items.Single().Id;
        SaveOrderDto Rename(string name) => new(name, Today, null, [new OrderItemInputDto(lineId, null, 1, null)], null);

        var demo = await WithOrders(Business, s => s.UpdateOrderAsync(order.Id, Rename("בדיקה - דנה")));
        Assert.True(demo.IsTest);
        Assert.Equal(order.Number, demo.Number);
        Assert.True((await WithOrders(Business, s => s.GetOrdersAsync("all", order.Number.ToString(), null, null))).Orders
            .Single(o => o.Id == order.Id).IsTest);

        var real = await WithOrders(Business, s => s.UpdateOrderAsync(order.Id, Rename("דנה כהן")));
        Assert.False(real.IsTest);
    }

    [Fact]
    public async Task Numbers_ConcurrentCreatesGetDistinctNumbers()
    {
        await SetupAsync();
        var product = await NewProductAsync("מקביל");

        var orders = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)))));

        Assert.Equal(6, orders.Select(o => o.Number).Distinct().Count());
    }

    [Fact]
    public async Task Discount_ByPercent_AndByFinalAmount_AndInvalidOnesAreRejected()
    {
        await SetupAsync();
        var product = await NewProductAsync("הנחה");

        var percent = await WithOrders(Business, s => s.CreateOrderAsync(
            Order(product.Id, discount: new OrderDiscountDto(DiscountMode.Percent, 10, "לקוחה חוזרת"))));
        Assert.Equal(1350, percent.FinalAmount);
        Assert.True(percent.HasDiscount);
        Assert.Equal(150, percent.DiscountAmount);
        Assert.Equal("לקוחה חוזרת", percent.DiscountReason);

        var final = await WithOrders(Business, s => s.CreateOrderAsync(
            Order(product.Id, discount: new OrderDiscountDto(DiscountMode.FinalAmount, 1200, null))));
        Assert.Equal(1200, final.FinalAmount);
        Assert.Equal(20, final.DiscountPercent);

        var none = await WithOrders(Business, s => s.CreateOrderAsync(
            Order(product.Id, discount: new OrderDiscountDto(DiscountMode.Percent, 0, "לא נחשב"))));
        Assert.False(none.HasDiscount);
        Assert.Null(none.DiscountReason);

        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.CreateOrderAsync(
            Order(product.Id, discount: new OrderDiscountDto(DiscountMode.Percent, 101, null)))));
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.CreateOrderAsync(
            Order(product.Id, discount: new OrderDiscountDto(DiscountMode.FinalAmount, 1501, null)))));
    }

    [Fact]
    public async Task LinePrice_CanBeOverridden_AndIsKept()
    {
        await SetupAsync();
        var product = await NewProductAsync("מחיר מוסכם");

        var order = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, price: 1234.5m)));

        Assert.Equal(1234.5m, Assert.Single(order.Items).UnitPrice);
        Assert.Equal(1234.5m, order.Amount);
    }

    [Fact]
    public async Task LineNote_IsSavedOnCreate_CanBeEditedAndCleared_AndSurvivesOtherEdits()
    {
        await SetupAsync();
        var product = await NewProductAsync("הערה לפריט");

        var created = await WithOrders(Business, s => s.CreateOrderAsync(new SaveOrderDto(
            "דנה", Today, null, [new OrderItemInputDto(null, product.Id, 1, null, "  חריטה: לנצח  ")], null)));
        var line = Assert.Single(created.Items);
        Assert.Equal("חריטה: לנצח", line.Note);

        var edited = await WithOrders(Business, s => s.UpdateOrderAsync(created.Id, new SaveOrderDto(
            "דנה", Today, null, [new OrderItemInputDto(line.Id, null, 2, null, "מידה 14")], null)));
        Assert.Equal("מידה 14", Assert.Single(edited.Items).Note);

        var cleared = await WithOrders(Business, s => s.UpdateOrderAsync(created.Id, new SaveOrderDto(
            "דנה", Today, null, [new OrderItemInputDto(line.Id, null, 2, null, "   ")], null)));
        Assert.Null(Assert.Single(cleared.Items).Note);
    }

    [Fact]
    public async Task Update_KeepsExistingLinesFrozen_AddsAndRemovesLines_AndRecalculates()
    {
        await SetupAsync();
        var a = await NewProductAsync("עריכה א", 1000);
        var b = await NewProductAsync("עריכה ב", 500);

        var created = await WithOrders(Business, s => s.CreateOrderAsync(Order(a.Id)));
        var lineId = created.Items.Single().Id;
        await WithProducts(s => s.UpdateSitePriceAsync(a.Id, new UpdateSitePriceDto(7777)));

        var updated = await WithOrders(Business, s => s.UpdateOrderAsync(created.Id, new SaveOrderDto(
            "דנה כהן", Today, "הערה",
            [new OrderItemInputDto(lineId, null, 3, null), new OrderItemInputDto(null, b.Id, 1, null)], null)));

        Assert.Equal(2, updated.Items.Count);
        Assert.Equal(1000, updated.Items[0].UnitPrice);          // still the frozen price, not 7777
        Assert.Equal(3, updated.Items[0].Quantity);
        Assert.Equal(500, updated.Items[1].UnitPrice);
        Assert.Equal(3500, updated.Amount);
        Assert.Equal("דנה כהן", updated.Customer);
        Assert.Equal(created.Number, updated.Number);

        var removed = await WithOrders(Business, s => s.UpdateOrderAsync(created.Id, new SaveOrderDto(
            "דנה כהן", Today, null, [new OrderItemInputDto(null, b.Id, 2, null)], null)));
        Assert.Equal("עריכה ב", Assert.Single(removed.Items).Name);
        Assert.Equal(1000, removed.Amount);
    }

    [Fact]
    public async Task Status_InProgressStartsAtTheFirstStage_AdvancesThroughThem_ThenBecomesReady()
    {
        await SetupAsync();
        var order = await NewOrderAsync("שלבים");

        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.AdvanceStageAsync(order.Id)));

        var started = await WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.InProgress));
        Assert.Equal("יציקה", started.PreparationStage);

        var second = await WithOrders(Business, s => s.AdvanceStageAsync(order.Id));
        Assert.Equal("שיבוץ", second.PreparationStage);
        var third = await WithOrders(Business, s => s.AdvanceStageAsync(order.Id));
        Assert.Equal("ליטוש", third.PreparationStage);

        var ready = await WithOrders(Business, s => s.AdvanceStageAsync(order.Id));
        Assert.Equal(OrderStatus.Ready, ready.Status);
        Assert.Null(ready.PreparationStage);
    }

    [Fact]
    public async Task Stage_CanBeSetDirectly_ForwardAndBack_ButOnlyToDefinedStages_AndOnlyInProgress()
    {
        await SetupAsync();
        var order = await NewOrderAsync("שלב ישיר");

        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.SetStageAsync(order.Id, "ליטוש")));

        await WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.InProgress));
        Assert.Equal("ליטוש", (await WithOrders(Business, s => s.SetStageAsync(order.Id, "ליטוש"))).PreparationStage);
        Assert.Equal("יציקה", (await WithOrders(Business, s => s.SetStageAsync(order.Id, "יציקה"))).PreparationStage);
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.SetStageAsync(order.Id, "שלב שלא קיים")));
    }

    [Fact]
    public async Task Status_CompleteLocksTheOrder_AndReopeningUnlocksIt()
    {
        await SetupAsync();
        var product = await NewProductAsync("נעילה");
        var order = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));

        await WithOrders(Business, s => s.SetReceiptSentAsync(order.Id, true));
        var done = await WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.Completed));
        Assert.True(done.IsCompleted);
        Assert.NotNull(done.CompletedDate);

        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.UpdateOrderAsync(order.Id, Order(product.Id, qty: 5))));

        var reopened = await WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.Ready));
        Assert.False(reopened.IsCompleted);
        Assert.Null(reopened.CompletedDate);
        var edited = await WithOrders(Business, s => s.UpdateOrderAsync(order.Id,
            new SaveOrderDto("דנה", Today, null, [new OrderItemInputDto(order.Items.Single().Id, null, 5, null)], null)));
        Assert.Equal(7500, edited.Amount);
    }

    [Fact]
    public async Task Receipt_IsRequiredToComplete_AndCannotBeRemovedFromACompletedOrder()
    {
        await SetupAsync();
        var order = await NewOrderAsync("קבלה");
        Assert.False(order.ReceiptSent);

        // No receipt, no completion.
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.Completed)));
        Assert.False((await WithOrders(Business, s => s.GetOrderAsync(order.Id))).IsCompleted);

        Assert.True((await WithOrders(Business, s => s.SetReceiptSentAsync(order.Id, true))).ReceiptSent);
        Assert.True((await WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.Completed))).IsCompleted);

        // Once completed the mark stays; it can only be changed after reopening the order.
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.SetReceiptSentAsync(order.Id, false)));
        await WithOrders(Business, s => s.UpdateStatusAsync(order.Id, OrderStatus.Ready));
        Assert.False((await WithOrders(Business, s => s.SetReceiptSentAsync(order.Id, false))).ReceiptSent);
    }

    [Fact]
    public async Task SoftDelete_HidesTheOrder_ButKeepsTheRow()
    {
        await SetupAsync();
        var order = await NewOrderAsync("מחיקה");

        await WithOrders(Business, async s => { await s.DeleteOrderAsync(order.Id); return 0; });

        await Assert.ThrowsAsync<NotFoundException>(() => WithOrders(Business, s => s.GetOrderAsync(order.Id)));
        Assert.DoesNotContain((await WithOrders(Business, s => s.GetOrdersAsync("all", null, null, null))).Orders, o => o.Id == order.Id);
        await using var db = pg.NewDb();
        Assert.True((await db.Orders.SingleAsync(o => o.Id == order.Id)).IsDeleted);
    }

    [Fact]
    public async Task List_FiltersByStatus_Period_AndSearch_AndSummarizesWhatItShows()
    {
        await SetupAsync();
        var product = await NewProductAsync("סינון", 1000);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var january = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, customer: $"ינואר {tag}", date: new(2025, 1, 15))));
        var march = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, qty: 2, customer: $"מרץ {tag}", date: new(2025, 3, 2))));
        var done = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, customer: $"הושלם {tag}", date: new(2025, 3, 20))));
        await WithOrders(Business, s => s.SetReceiptSentAsync(done.Id, true));
        await WithOrders(Business, s => s.UpdateStatusAsync(done.Id, OrderStatus.Completed));

        // Search narrows to this test's orders; "all" includes the completed one.
        var all = await WithOrders(Business, s => s.GetOrdersAsync("all", tag, null, null));
        Assert.Equal(3, all.Summary.Count);

        var active = await WithOrders(Business, s => s.GetOrdersAsync(null, tag, null, null));
        Assert.DoesNotContain(active.Orders, o => o.Id == done.Id);
        var completed = await WithOrders(Business, s => s.GetOrdersAsync("completed", tag, null, null));
        Assert.Equal([done.Id], completed.Orders.Select(o => o.Id));

        // One month: March 2025 (from first to last day).
        var m = await WithOrders(Business, s => s.GetOrdersAsync("all", tag, new(2025, 3, 1), new(2025, 3, 31)));
        Assert.Equal(2, m.Summary.Count);
        Assert.Equal(3000, m.Summary.TotalFinalAmount);
        Assert.DoesNotContain(m.Orders, o => o.Id == january.Id);

        // Newest first, and a search by order number finds exactly that order.
        Assert.Equal(new[] { done.Id, march.Id }, m.Orders.Select(o => o.Id));
        var byNumber = await WithOrders(Business, s => s.GetOrdersAsync("all", march.Number.ToString(), null, null));
        Assert.Contains(byNumber.Orders, o => o.Id == march.Id);

        Assert.Contains(2025, await WithOrders(Business, s => s.GetYearsAsync()));
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.GetOrdersAsync("bogus", null, null, null)));
    }

    [Fact]
    public async Task Search_TreatsPercentAndUnderscoreAsPlainText()
    {
        await SetupAsync();
        var product = await NewProductAsync("מיוחדים");
        await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id, customer: "דנה")));

        var result = await WithOrders(Business, s => s.GetOrdersAsync("all", "%", null, null));

        Assert.DoesNotContain(result.Orders, o => o.Customer == "דנה");
    }

    [Fact]
    public async Task Orders_AreIsolatedBetweenBusinesses()
    {
        await SetupAsync();
        var order = await NewOrderAsync("בידוד");

        await Assert.ThrowsAsync<NotFoundException>(() => WithOrders(pg.BusinessB, s => s.GetOrderAsync(order.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => WithOrders(pg.BusinessB, s => s.UpdateStatusAsync(order.Id, OrderStatus.Ready)));
        await Assert.ThrowsAsync<NotFoundException>(() => WithOrders(pg.BusinessB, s => s.SetReceiptSentAsync(order.Id, true)));
        Assert.DoesNotContain((await WithOrders(pg.BusinessB, s => s.GetOrdersAsync("all", null, null, null))).Orders, o => o.Id == order.Id);
    }

    [Fact]
    public async Task DeletingAProduct_KeepsTheOrderLine_WithoutTheLink()
    {
        await SetupAsync();
        var product = await NewProductAsync("יימחק");
        var order = await WithOrders(Business, s => s.CreateOrderAsync(Order(product.Id)));

        await WithProducts(async s => { await s.DeleteProductAsync(product.Id); return 0; });

        var read = await WithOrders(Business, s => s.GetOrderAsync(order.Id));
        var line = Assert.Single(read.Items);
        Assert.Null(line.ProductId);
        Assert.Equal("יימחק", line.Name);
        Assert.Equal(1500, line.UnitPrice);
    }

    [Fact]
    public async Task Create_RejectsUnknownProducts_AndEmptyOrders()
    {
        await SetupAsync();
        await SetupAsync(pg.BusinessB);

        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(Business, s => s.CreateOrderAsync(Order(Guid.NewGuid()))));

        var foreign = await NewOrderAsync("זר");
        await Assert.ThrowsAsync<BadRequestException>(() => WithOrders(pg.BusinessB, s => s.CreateOrderAsync(
            Order(foreign.Items.Single().ProductId!.Value))));
    }
}
