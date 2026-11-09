using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Analytics;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;
using JewelryManager.Api.Features.Tasks;
using JewelryManager.Api.Features.Tasks.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>The new server-side pieces behind the coach (task fields and history, analytics) on the real test database.</summary>
[Collection("postgres")]
public class CoachApiTests(PostgresFixture pg)
{
    private async Task<Guid> NewBusinessAsync()
    {
        await using var db = pg.NewDb();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Businesses.Add(new Business { Id = id, Name = "coach-api-" + id.ToString("N")[..6], CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<T> Tasks<T>(Guid business, Func<TasksService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new TasksService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task TasksDo(Guid business, Func<TasksService, Task> action)
    {
        await using var db = pg.NewDb();
        await action(new TasksService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task<T> Analytics<T>(Guid business, Func<AnalyticsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(business);
        var pricing = new PricingService(db, tenant);
        return await action(new AnalyticsService(db, tenant, new ProductsService(db, tenant, pricing), pricing));
    }

    private static SaveTaskDto Dto(string title, DateOnly? due = null, Guid? parent = null, WorkTaskStatus status = WorkTaskStatus.New) =>
        new(title, null, status, null, null, due, parent);

    // ── Tasks ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Moving_a_date_later_counts_as_a_postponement_and_leaves_a_history()
    {
        var business = await NewBusinessAsync();
        var task = await Tasks(business, s => s.CreateTaskAsync(Dto("להזמין כסף", new DateOnly(2026, 11, 20))));

        await Tasks(business, s => s.UpdateTaskAsync(task.Id, Dto("להזמין כסף", new DateOnly(2026, 11, 27))));
        var again = await Tasks(business, s => s.UpdateTaskAsync(task.Id, Dto("להזמין כסף", new DateOnly(2026, 12, 4))));
        var earlier = await Tasks(business, s => s.UpdateTaskAsync(task.Id, Dto("להזמין כסף", new DateOnly(2026, 12, 1))));

        Assert.Equal(2, again.DeferCount);
        Assert.Equal(2, earlier.DeferCount);

        await using var db = pg.NewDb();
        var history = await db.TaskChanges.Where(c => c.BusinessId == business && c.TaskId == task.Id && c.Field == "due").ToListAsync();
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task An_update_without_a_date_keeps_the_date_it_had()
    {
        var business = await NewBusinessAsync();
        var task = await Tasks(business, s => s.CreateTaskAsync(Dto("משימה", new DateOnly(2026, 11, 20))));

        var updated = await Tasks(business, s => s.UpdateTaskAsync(task.Id, Dto("משימה בשם חדש")));

        Assert.Equal(new DateOnly(2026, 11, 20), updated.DueDate);
        Assert.Equal("משימה בשם חדש", updated.Title);
    }

    [Fact]
    public async Task Snoozing_and_setting_aside_do_not_look_like_work_on_the_task_and_bringing_it_back_does()
    {
        var business = await NewBusinessAsync();
        var task = await Tasks(business, s => s.CreateTaskAsync(Dto("משימה")));
        await Task.Delay(50);

        var snoozed = await Tasks(business, s => s.CoachActionAsync(task.Id, new TaskCoachActionDto("snooze", DateTime.UtcNow.AddDays(7))));
        var parked = await Tasks(business, s => s.CoachActionAsync(task.Id, new TaskCoachActionDto("park")));
        Assert.Equal(task.UpdatedAt, snoozed.UpdatedAt);
        Assert.Equal(task.UpdatedAt, parked.UpdatedAt);
        Assert.NotNull(parked.ParkedAt);

        var back = await Tasks(business, s => s.CoachActionAsync(task.Id, new TaskCoachActionDto("unpark")));
        Assert.Null(back.ParkedAt);
        Assert.Null(back.SnoozedUntil);
        Assert.True(back.UpdatedAt > task.UpdatedAt);
    }

    [Fact]
    public async Task A_snooze_must_be_in_the_future_and_within_ninety_days_and_other_actions_are_refused()
    {
        var business = await NewBusinessAsync();
        var task = await Tasks(business, s => s.CreateTaskAsync(Dto("משימה")));

        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Tasks(business, s => s.CoachActionAsync(task.Id, new TaskCoachActionDto("snooze", DateTime.UtcNow.AddDays(-1)))));
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Tasks(business, s => s.CoachActionAsync(task.Id, new TaskCoachActionDto("snooze", DateTime.UtcNow.AddDays(200)))));
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Tasks(business, s => s.CoachActionAsync(task.Id, new TaskCoachActionDto("delete"))));
    }

    [Fact]
    public async Task A_batch_creates_all_steps_under_the_big_task_and_deleting_the_big_one_frees_them()
    {
        var business = await NewBusinessAsync();
        var parent = await Tasks(business, s => s.CreateTaskAsync(Dto("לעדכן את האתר")));

        var steps = await Tasks(business, s => s.CreateBatchAsync(new BatchTasksDto([Dto("צעד 1", parent: parent.Id), Dto("צעד 2", parent: parent.Id), Dto("צעד 3", parent: parent.Id)])));
        Assert.Equal(3, steps.Count);
        Assert.All(steps, t => Assert.Equal(parent.Id, t.ParentTaskId));
        Assert.Equal(["צעד 1", "צעד 2", "צעד 3"], steps.Select(t => t.Title));

        await TasksDo(business, s => s.DeleteTaskAsync(parent.Id));
        var freed = await Tasks(business, s => s.GetTaskAsync(steps[0].Id));
        Assert.Null(freed.ParentTaskId);
    }

    [Fact]
    public async Task A_batch_with_one_bad_step_creates_nothing()
    {
        var business = await NewBusinessAsync();
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Tasks(business, s => s.CreateBatchAsync(new BatchTasksDto([Dto("טוב"), Dto("   ")]))));
        Assert.Empty(await Tasks(business, s => s.GetTasksAsync(null, null)));
    }

    [Fact]
    public async Task A_step_cannot_hang_on_another_businesss_task()
    {
        var mine = await NewBusinessAsync();
        var theirs = await NewBusinessAsync();
        var foreign = await Tasks(theirs, s => s.CreateTaskAsync(Dto("שלהם")));

        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Tasks(mine, s => s.CreateTaskAsync(Dto("צעד", parent: foreign.Id))));
    }

    [Fact]
    public async Task Taking_back_a_batch_removes_only_those_tasks_and_only_from_this_business()
    {
        var mine = await NewBusinessAsync();
        var theirs = await NewBusinessAsync();
        var batch = await Tasks(mine, s => s.CreateBatchAsync(new BatchTasksDto([Dto("א"), Dto("ב")])));
        var other = await Tasks(theirs, s => s.CreateTaskAsync(Dto("שלהם")));
        var keep = await Tasks(mine, s => s.CreateTaskAsync(Dto("נשארת")));

        await TasksDo(mine, s => s.DeleteBatchAsync(new BatchDeleteTasksDto([.. batch.Select(t => t.Id), other.Id])));

        var remaining = await Tasks(mine, s => s.GetTasksAsync(null, null));
        Assert.Equal([keep.Id], remaining.Select(t => t.Id));
        Assert.Single(await Tasks(theirs, s => s.GetTasksAsync(null, null)));
    }

    [Fact]
    public async Task Finishing_one_task_changes_one_task()
    {
        var business = await NewBusinessAsync();
        var a = await Tasks(business, s => s.CreateTaskAsync(Dto("א")));
        var b = await Tasks(business, s => s.CreateTaskAsync(Dto("ב")));
        var c = await Tasks(business, s => s.CreateTaskAsync(Dto("ג")));

        await Tasks(business, s => s.UpdateStatusAsync(b.Id, WorkTaskStatus.Completed));

        var all = (await Tasks(business, s => s.GetTasksAsync(null, null))).ToDictionary(t => t.Id);
        Assert.Equal(WorkTaskStatus.Completed, all[b.Id].Status);
        Assert.Equal(WorkTaskStatus.New, all[a.Id].Status);
        Assert.Equal(WorkTaskStatus.New, all[c.Id].Status);
        Assert.Equal(["א", "ב", "ג"], new[] { all[a.Id].Title, all[b.Id].Title, all[c.Id].Title });
        Assert.Equal(2, (await Tasks(business, s => s.GetTasksAsync(WorkTaskStatus.New, null))).Count);
    }

    // ── Analytics ─────────────────────────────────────────────────────────────

    private async Task AddOrdersAsync(Guid business, params (int Number, DateOnly Date, decimal Amount, string? Customer, string Item, bool Deleted, bool Pending, OrderSource Source)[] orders)
    {
        await using var db = pg.NewDb();
        var now = DateTime.UtcNow;
        foreach (var o in orders)
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(), BusinessId = business, Number = o.Number, Date = o.Date, Customer = o.Customer,
                Amount = o.Amount, FinalAmount = o.Amount, Status = OrderStatus.Completed, IsCompleted = true,
                IsDeleted = o.Deleted, IsPendingApproval = o.Pending, Source = o.Source, CreatedAt = now, UpdatedAt = now,
                Items = [new OrderLineItem { Id = Guid.NewGuid(), BusinessId = business, Name = o.Item, Type = "טבעת", Material = "כסף", UnitPrice = o.Amount, Quantity = 1 }],
            });
        await db.SaveChangesAsync();
    }

    private static (int, DateOnly, decimal, string?, string, bool, bool, OrderSource) Order(int number, DateOnly date, decimal amount, string? customer = "דנה", string item = "טבעת בדיקה",
        bool deleted = false, bool pending = false, OrderSource source = OrderSource.Manual) => (number, date, amount, customer, item, deleted, pending, source);

    [Fact]
    public async Task Sales_count_only_real_orders_and_group_by_week_or_month()
    {
        var business = await NewBusinessAsync();
        await AddOrdersAsync(business,
            Order(1001, new DateOnly(2026, 11, 2), 1000),
            Order(1002, new DateOnly(2026, 11, 4), 500),
            Order(1003, new DateOnly(2026, 11, 12), 300),
            Order(1004, new DateOnly(2026, 10, 30), 200),
            Order(500, new DateOnly(2026, 11, 3), 9999),
            Order(1005, new DateOnly(2026, 11, 3), 9999, deleted: true),
            Order(1006, new DateOnly(2026, 11, 3), 9999, pending: true));

        var months = await Analytics(business, s => s.SalesAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), "month"));
        Assert.Equal([("2026-10-01", 1, 200m), ("2026-11-01", 3, 1800m)], months.Select(m => (m.Period, m.Orders, m.Revenue)));

        var weeks = await Analytics(business, s => s.SalesAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "week"));
        Assert.Equal([("2026-11-02", 2, 1500m), ("2026-11-09", 1, 300m)], weeks.Select(m => (m.Period, m.Orders, m.Revenue)));

        var days = await Analytics(business, s => s.SalesAsync(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), "day"));
        Assert.Single(days);
    }

    [Fact]
    public async Task Bad_sales_requests_are_refused()
    {
        var business = await NewBusinessAsync();
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Analytics(business, s => s.SalesAsync(new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 1), "day")));
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Analytics(business, s => s.SalesAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "year")));
    }

    [Fact]
    public async Task Another_business_never_sees_these_numbers()
    {
        var mine = await NewBusinessAsync();
        var theirs = await NewBusinessAsync();
        await AddOrdersAsync(mine, Order(1001, new DateOnly(2026, 11, 2), 1000));

        Assert.Empty(await Analytics(theirs, s => s.SalesAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "month")));
        Assert.Empty((await Analytics(theirs, s => s.SellersAsync(6))).Best);
    }

    [Fact]
    public async Task Growth_compares_recent_orders_with_before_and_finds_repeat_customers()
    {
        var business = await NewBusinessAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddOrdersAsync(business,
            Order(1001, today.AddDays(-2), 1000, "דנה"),
            Order(1002, today.AddDays(-10), 600, "דנה"),
            Order(1003, today.AddDays(-20), 800, "רונית", source: OrderSource.Shopify),
            Order(1004, today.AddDays(-50), 400, "מיכל"),
            Order(1005, today.AddDays(-80), 400, "שרה"),
            Order(1006, today.AddDays(-100), 400, "נועה"));

        var growth = await Analytics(business, s => s.GrowthAsync());

        Assert.Equal(3, growth.OrdersLast30Days);
        Assert.Equal(1m, growth.OrdersPrior30DayAverage);
        Assert.Equal(0.2m, growth.RepeatCustomerRate);
        Assert.Equal("כסף", growth.TopMaterial);
        Assert.Equal(0, growth.Products);
    }

    [Fact]
    public async Task Best_sellers_are_counted_and_unsold_old_products_are_called_slow()
    {
        var business = await NewBusinessAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddOrdersAsync(business,
            Order(1001, today.AddDays(-5), 1000, item: "טבעת א"), Order(1002, today.AddDays(-9), 1000, item: "טבעת א"),
            Order(1003, today.AddDays(-12), 400, item: "עגיל ב"));

        await using (var db = pg.NewDb())
        {
            var old = DateTime.UtcNow.AddMonths(-10);
            db.Products.Add(new Product { Id = Guid.NewGuid(), BusinessId = business, Type = "צמיד", Name = "צמיד ישן", Material = "כסף", SitePrice = 300, CreatedAt = old, UpdatedAt = old });
            db.Products.Add(new Product { Id = Guid.NewGuid(), BusinessId = business, Type = "צמיד", Name = "צמיד חדש", Material = "כסף", SitePrice = 300, CreatedAt = DateTime.UtcNow.AddDays(-5), UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var sellers = await Analytics(business, s => s.SellersAsync(6));

        Assert.Equal("טבעת א", sellers.Best[0].Name);
        Assert.Equal(2, sellers.Best[0].Quantity);
        Assert.Equal(["צמיד ישן"], sellers.Slow.Select(p => p.Name));
    }

    [Fact]
    public async Task Customers_from_about_a_year_ago_are_found_with_their_item()
    {
        var business = await NewBusinessAsync();
        var yearAgo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-365);
        await AddOrdersAsync(business,
            Order(1001, yearAgo.AddDays(2), 900, "דנה", "טבעת אירוסין"),
            Order(1002, yearAgo.AddDays(40), 900, "רונית"),
            Order(1003, yearAgo, 100, null));

        var rows = await Analytics(business, s => s.AnniversariesAsync(365, 7));

        var row = Assert.Single(rows);
        Assert.Equal(("דנה", "טבעת אירוסין"), (row.Customer, row.Item));
    }

    private async Task SetUpPricingAsync(Guid business)
    {
        await using (var db = pg.NewDb())
        {
            var now = DateTime.UtcNow;
            db.Settings.Add(new Settings { Id = Guid.NewGuid(), BusinessId = business, CreatedAt = now, UpdatedAt = now });
            db.Collections.Add(new Collection { Id = Guid.NewGuid(), BusinessId = business, Name = "כללי", Key = "general", IsPermanent = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        await using var db2 = pg.NewDb();
        await new SettingsService(db2, PostgresFixture.TenantFor(business)).UpdateSettingsAsync(new UpdateSettingsDto(
            Materials: new() { ["כסף"] = new(8, 1, 1.5m), ["זהב"] = new(330, 2, 1.8m) },
            LaborHourRate: 100,
            PricingAdditions: [new PricingAdditionDto("אריזה", 0, [new("קופסה", 3)])],
            FeesItems: [new("עמלת סליקה", 3, true, FeeKeys.CardFee), new("מע\"מ", 18, true, FeeKeys.Vat), new("עמלת עלויות קבועות", 17, true, FeeKeys.FixedExpenses)],
            ProfitFloorPercent: 30,
            PreparationStages: ["יציקה"],
            ProductAdditionTypes: [new("אבן", false)]));
    }

    private async Task<Guid> AddProductAsync(Guid business, string name, string material, decimal weight, decimal sitePrice)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(business);
        var general = await db.Collections.AsNoTracking().FirstAsync(c => c.BusinessId == business && c.Key == "general");
        var product = await new ProductsService(db, tenant, new PricingService(db, tenant))
            .CreateProductAsync(new SaveProductDto("טבעת", name, material, weight, 0, sitePrice, [], [general.Id]));
        return product.Id;
    }

    [Fact]
    public async Task A_metal_price_change_moves_the_recommended_prices_and_flags_products_that_fall_below_the_floor()
    {
        var business = await NewBusinessAsync();
        await SetUpPricingAsync(business);

        // The site price is set at exactly the recommended price today, so any rise in cost takes it under the floor.
        var probe = await Analytics(business, s => s.MaterialsAsync());
        Assert.Contains(probe, m => m.Name == "כסף" && m.PricePerGram == 8);
        var id = await AddProductAsync(business, "טבעת כסף", "כסף", 10, 1);
        await using (var db = pg.NewDb())
        {
            var tenant = PostgresFixture.TenantFor(business);
            var recommended = (await new ProductsService(db, tenant, new PricingService(db, tenant)).GetProductAsync(id)).Price!.RecommendedPrice;
            await new ProductsService(db, tenant, new PricingService(db, tenant)).UpdateSitePriceAsync(id, new UpdateSitePriceDto(recommended));
        }

        var up = await Analytics(business, s => s.WhatIfAsync(new WhatIfRequest([new MaterialChange("כסף", null, 20)])));
        Assert.Equal(1, up.Products);
        Assert.True(up.AverageChangePercent > 0);
        Assert.Equal(1, up.NewlyBelowProfitFloor);
        Assert.Equal(["טבעת כסף"], up.NewlyBelowExamples);

        var down = await Analytics(business, s => s.WhatIfAsync(new WhatIfRequest([new MaterialChange("כסף", 4, null)])));
        Assert.True(down.AverageChangePercent < 0);
        Assert.Equal(0, down.NewlyBelowProfitFloor);

        var none = await Analytics(business, s => s.WhatIfAsync(new WhatIfRequest([new MaterialChange("זהב", null, 5)])));
        Assert.Equal(0, none.Products);
    }

    [Fact]
    public async Task What_if_refuses_an_unknown_material_or_an_empty_change()
    {
        var business = await NewBusinessAsync();
        await SetUpPricingAsync(business);

        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Analytics(business, s => s.WhatIfAsync(new WhatIfRequest([new MaterialChange("פלטינה", null, 5)]))));
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Analytics(business, s => s.WhatIfAsync(new WhatIfRequest([new MaterialChange("כסף", null, null)]))));
        await Assert.ThrowsAsync<JewelryManager.Api.Common.Exceptions.BadRequestException>(() =>
            Analytics(business, s => s.WhatIfAsync(new WhatIfRequest([new MaterialChange("כסף", null, -100)]))));
    }

    [Fact]
    public async Task The_discount_ceiling_keeps_the_profit_floor_and_lists_products_already_under_it()
    {
        var business = await NewBusinessAsync();
        await SetUpPricingAsync(business);
        await AddProductAsync(business, "טבעת יקרה", "כסף", 10, 1000);
        await AddProductAsync(business, "טבעת זולה", "כסף", 10, 1);

        var ceiling = await Analytics(business, s => s.DiscountCeilingAsync());

        Assert.Equal(30, ceiling.ProfitFloorPercent);
        Assert.Equal(["טבעת זולה"], ceiling.AlreadyBelowFloor);
        var row = Assert.Single(ceiling.ByType);
        Assert.Equal(0, row.SafeForAllPercent);
        Assert.True(row.MedianPercent >= 0);
    }
}
