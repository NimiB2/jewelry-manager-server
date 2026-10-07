using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Tasks;
using JewelryManager.Api.Features.Tasks.Dtos;

namespace JewelryManager.Tests.Integration;

[Collection("postgres")]
public class TasksTests(PostgresFixture pg)
{
    private async Task<T> WithTasks<T>(Guid business, Func<TasksService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new TasksService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task<Guid> NewOrderAsync(Guid business, int number)
    {
        await using var db = pg.NewDb();
        var now = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(), BusinessId = business, Number = number, Customer = "דנה",
            Date = new DateOnly(2026, 10, 7), CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    [Fact]
    public async Task Task_CanStandAlone_OrHangOnAnOrder()
    {
        var b = pg.BusinessE;
        var orderId = await NewOrderAsync(b, 7001);

        var alone = await WithTasks(b, s => s.CreateTaskAsync(new SaveTaskDto("  לצלם מוצרים ", null, WorkTaskStatus.New, null)));
        Assert.Equal("לצלם מוצרים", alone.Title);
        Assert.Null(alone.OrderId);

        var linked = await WithTasks(b, s => s.CreateTaskAsync(new SaveTaskDto("ליטוש", "עד חמישי", WorkTaskStatus.InProgress, orderId)));
        Assert.Equal(7001, linked.OrderNumber);
        Assert.Equal("דנה", linked.OrderCustomer);

        var forOrder = await WithTasks(b, s => s.GetTasksAsync(null, orderId));
        Assert.Single(forOrder);
    }

    [Fact]
    public async Task Status_FlowsThroughThreeValues_AndCompletionIsStamped()
    {
        var b = pg.BusinessE;
        var task = await WithTasks(b, s => s.CreateTaskAsync(new SaveTaskDto("סטטוס", null, WorkTaskStatus.New, null)));

        var inProgress = await WithTasks(b, s => s.UpdateStatusAsync(task.Id, WorkTaskStatus.InProgress));
        Assert.Null(inProgress.CompletedAt);

        var done = await WithTasks(b, s => s.UpdateStatusAsync(task.Id, WorkTaskStatus.Completed));
        Assert.NotNull(done.CompletedAt);

        var reopened = await WithTasks(b, s => s.UpdateStatusAsync(task.Id, WorkTaskStatus.New));
        Assert.Null(reopened.CompletedAt);

        var open = await WithTasks(b, s => s.GetTasksAsync(WorkTaskStatus.New, null));
        Assert.Contains(open, t => t.Id == task.Id);
    }

    [Fact]
    public async Task Validation_AndTenantIsolation()
    {
        var orderOfF = await NewOrderAsync(pg.BusinessF, 7002);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            WithTasks(pg.BusinessE, s => s.CreateTaskAsync(new SaveTaskDto("   ", null, WorkTaskStatus.New, null))));
        await Assert.ThrowsAsync<BadRequestException>(() =>
            WithTasks(pg.BusinessE, s => s.CreateTaskAsync(new SaveTaskDto("x", null, WorkTaskStatus.New, orderOfF))));

        var task = await WithTasks(pg.BusinessE, s => s.CreateTaskAsync(new SaveTaskDto("פרטית", null, WorkTaskStatus.New, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => WithTasks(pg.BusinessF, s => s.GetTaskAsync(task.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => WithTasks(pg.BusinessF, async s => { await s.DeleteTaskAsync(task.Id); return 0; }));

        await WithTasks(pg.BusinessE, async s => { await s.DeleteTaskAsync(task.Id); return 0; });
        await Assert.ThrowsAsync<NotFoundException>(() => WithTasks(pg.BusinessE, s => s.GetTaskAsync(task.Id)));
    }
}
