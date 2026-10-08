using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Tasks.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Tasks;

public class TasksService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<List<TaskResponse>> GetTasksAsync(WorkTaskStatus? status, Guid? orderId)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Tasks.AsNoTracking().Where(t => t.BusinessId == businessId);
        if (status is { } s) query = query.Where(t => t.Status == s);
        if (orderId is { } o) query = query.Where(t => t.OrderId == o);

        var tasks = await query.ToListAsync();
        var orders = await LoadOrdersAsync(businessId, tasks);

        // Open work first (in progress, then new), finished tasks last; newest first inside each group.
        return tasks
            .OrderBy(t => t.Status == WorkTaskStatus.Completed ? 1 : 0)
            .ThenBy(t => t.Status == WorkTaskStatus.InProgress ? 0 : 1)
            .ThenByDescending(t => t.CreatedAt)
            .Select(t => ToResponse(t, orders))
            .ToList();
    }

    public async Task<TaskResponse> GetTaskAsync(Guid id)
    {
        var businessId = tenant.GetBusinessId();
        var task = await FindOrThrowAsync(id, asNoTracking: true);
        return ToResponse(task, await LoadOrdersAsync(businessId, [task]));
    }

    public async Task<TaskResponse> CreateTaskAsync(SaveTaskDto dto)
    {
        var now = DateTime.UtcNow;
        var task = new TaskItem { Id = Guid.NewGuid(), BusinessId = tenant.GetBusinessId(), CreatedAt = now };
        await ApplyAsync(task, dto, now);

        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetTaskAsync(task.Id);
    }

    public async Task<TaskResponse> UpdateTaskAsync(Guid id, SaveTaskDto dto)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        await ApplyAsync(task, dto, DateTime.UtcNow);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetTaskAsync(id);
    }

    public async Task<TaskResponse> UpdateStatusAsync(Guid id, WorkTaskStatus status)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        SetStatus(task, status, DateTime.UtcNow);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetTaskAsync(id);
    }

    public async Task DeleteTaskAsync(Guid id)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        db.Tasks.Remove(task);
        await db.SaveChangesAsync();
    }

    // A task made from an order mirrors it: its status, title and life all come from the order.
    private static void ThrowIfAutomatic(TaskItem task)
    {
        if (task.IsAutomatic)
            throw new BadRequestException("משימה שנוצרה מהזמנה משתנה דרך ההזמנה עצמה");
    }

    private async Task ApplyAsync(TaskItem task, SaveTaskDto dto, DateTime now)
    {
        var title = dto.Title.Trim();
        if (title.Length == 0) throw new BadRequestException("A title is required");

        if (dto.OrderId is { } orderId)
        {
            var businessId = tenant.GetBusinessId();
            var exists = await db.Orders.AsNoTracking()
                .AnyAsync(o => o.Id == orderId && o.BusinessId == businessId && !o.IsDeleted);
            if (!exists) throw new BadRequestException("Order not found");
        }

        task.Title = title;
        task.Content = string.IsNullOrWhiteSpace(dto.Content) ? null : dto.Content.Trim();
        task.OrderId = dto.OrderId;
        SetStatus(task, dto.Status, now);
    }

    private static void SetStatus(TaskItem task, WorkTaskStatus status, DateTime now)
    {
        task.CompletedAt = status == WorkTaskStatus.Completed ? task.CompletedAt ?? now : null;

        task.Status = status;
        task.UpdatedAt = now;
    }

    private async Task<TaskItem> FindOrThrowAsync(Guid id, bool asNoTracking)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Tasks.AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == businessId)
            ?? throw new NotFoundException("Task not found");
    }

    private async Task<Dictionary<Guid, Order>> LoadOrdersAsync(Guid businessId, IEnumerable<TaskItem> tasks)
    {
        var ids = tasks.Where(t => t.OrderId != null).Select(t => t.OrderId!.Value).Distinct().ToList();
        return await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id);
    }

    private static TaskResponse ToResponse(TaskItem t, Dictionary<Guid, Order> orders)
    {
        Order? order = t.OrderId is { } id && orders.TryGetValue(id, out var found) ? found : null;
        return new TaskResponse(
            t.Id, t.Title, t.Content, t.Status, t.OrderId, order?.Number, order?.Customer, t.CreatedAt, t.CompletedAt, t.IsAutomatic, order?.Status);
    }
}
