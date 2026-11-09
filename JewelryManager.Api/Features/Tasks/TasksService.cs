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

        // Open work first, finished tasks last; the newest on top so a task just added is never buried.
        return tasks
            .OrderBy(t => t.Status == WorkTaskStatus.Completed ? 1 : 0)
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

    public async Task<TaskResponse> CreateTaskAsync(SaveTaskDto dto) => await CreateTaskAsync(dto, "app");

    public async Task<TaskResponse> CreateTaskAsync(SaveTaskDto dto, string source)
    {
        var now = DateTime.UtcNow;
        var task = new TaskItem { Id = Guid.NewGuid(), BusinessId = tenant.GetBusinessId(), CreatedAt = now };
        await ApplyAsync(task, dto, now, source, isNew: true);

        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetTaskAsync(task.Id);
    }

    /// <summary>Creates the small steps of a breakdown (or a checklist) together; either all exist or none.</summary>
    public async Task<List<TaskResponse>> CreateBatchAsync(BatchTasksDto dto)
    {
        var now = DateTime.UtcNow;
        var created = new List<TaskItem>();
        foreach (var item in dto.Tasks)
        {
            var task = new TaskItem { Id = Guid.NewGuid(), BusinessId = tenant.GetBusinessId(), CreatedAt = now };
            await ApplyAsync(task, item, now, "agent", isNew: true);
            created.Add(task);
        }

        db.Tasks.AddRange(created);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var businessId = tenant.GetBusinessId();
        var ids = created.Select(t => t.Id).ToList();
        var rows = await db.Tasks.AsNoTracking().Where(t => t.BusinessId == businessId && ids.Contains(t.Id)).ToListAsync();
        var orders = await LoadOrdersAsync(businessId, rows);
        return rows.OrderBy(t => ids.IndexOf(t.Id)).Select(t => ToResponse(t, orders)).ToList();
    }

    /// <summary>Takes back a batch the agent just created. Only tasks it made itself (not from an order) can go this way.</summary>
    public async Task DeleteBatchAsync(BatchDeleteTasksDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var tasks = await db.Tasks.Where(t => t.BusinessId == businessId && dto.Ids.Contains(t.Id) && !t.IsAutomatic).ToListAsync();
        foreach (var task in tasks) Record(task.Id, "deleted", task.Title, null, "agent");

        db.Tasks.RemoveRange(tasks);
        await db.SaveChangesAsync();
    }

    public async Task<TaskResponse> UpdateTaskAsync(Guid id, SaveTaskDto dto)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        await ApplyAsync(task, dto, DateTime.UtcNow, "app", isNew: false);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetTaskAsync(id);
    }

    public async Task<TaskResponse> UpdateStatusAsync(Guid id, WorkTaskStatus status)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        var old = task.Status;
        SetStatus(task, status, DateTime.UtcNow);
        if (old != status) Record(task.Id, "status", old.ToString(), status.ToString(), "app");

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetTaskAsync(id);
    }

    /// <summary>The agent's gentle controls: snooze, set aside, bring back, or clear the date.</summary>
    public async Task<TaskResponse> CoachActionAsync(Guid id, TaskCoachActionDto dto)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        var now = DateTime.UtcNow;

        switch (dto.Action.Trim().ToLowerInvariant())
        {
            // Snoozing and parking do not touch UpdatedAt: the agent's own handling must not look like she worked on it.
            case "snooze":
                var until = dto.Until ?? now.AddDays(7);
                if (until <= now || until > now.AddDays(90)) throw new BadRequestException("A snooze is between now and 90 days");
                Record(task.Id, "snoozed", task.SnoozedUntil?.ToString("O"), until.ToString("O"), "agent");
                task.SnoozedUntil = until;
                break;
            case "park":
                Record(task.Id, "parked", task.ParkedAt?.ToString("O"), now.ToString("O"), "agent");
                task.ParkedAt = now;
                break;
            case "unpark":
                Record(task.Id, "parked", task.ParkedAt?.ToString("O"), null, "agent");
                task.ParkedAt = null;
                task.SnoozedUntil = null;
                task.UpdatedAt = now;
                break;
            case "clear-due":
                Record(task.Id, "due", task.DueDate?.ToString("yyyy-MM-dd"), null, "agent");
                task.DueDate = null;
                task.UpdatedAt = now;
                break;
            default:
                throw new BadRequestException("Action must be snooze, park, unpark or clear-due");
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await GetTaskAsync(id);
    }

    public async Task DeleteTaskAsync(Guid id)
    {
        var task = await FindOrThrowAsync(id, asNoTracking: false);
        ThrowIfAutomatic(task);
        Record(task.Id, "deleted", task.Title, null, "app");
        db.Tasks.Remove(task);
        await db.SaveChangesAsync();
    }

    // A task made from an order mirrors it: its status, title and life all come from the order.
    private static void ThrowIfAutomatic(TaskItem task)
    {
        if (task.IsAutomatic)
            throw new BadRequestException("משימה שנוצרה מהזמנה משתנה דרך ההזמנה עצמה");
    }

    private async Task ApplyAsync(TaskItem task, SaveTaskDto dto, DateTime now, string source, bool isNew)
    {
        var title = dto.Title.Trim();
        if (title.Length == 0) throw new BadRequestException("A title is required");

        var businessId = tenant.GetBusinessId();
        if (dto.OrderId is { } orderId)
        {
            var exists = await db.Orders.AsNoTracking()
                .AnyAsync(o => o.Id == orderId && o.BusinessId == businessId && !o.IsDeleted);
            if (!exists) throw new BadRequestException("Order not found");
        }

        if (dto.ParentTaskId is { } parentId)
        {
            if (parentId == task.Id) throw new BadRequestException("A task cannot be its own step");
            var parentExists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == parentId && t.BusinessId == businessId);
            if (!parentExists) throw new BadRequestException("The big task was not found");
            task.ParentTaskId = parentId;
        }

        if (!isNew && task.Title != title) Record(task.Id, "title", task.Title, title, source);
        task.Title = title;
        task.Content = string.IsNullOrWhiteSpace(dto.Content) ? null : dto.Content.Trim();
        task.OrderId = dto.OrderId;
        task.Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category.Trim();

        // An empty date on an update keeps the old one; moving it later counts as a postponement.
        if (dto.DueDate is { } due)
        {
            if (!isNew && task.DueDate != due)
            {
                Record(task.Id, "due", task.DueDate?.ToString("yyyy-MM-dd"), due.ToString("yyyy-MM-dd"), source);
                if (task.DueDate is { } before && due > before) task.DeferCount++;
            }

            task.DueDate = due;
        }

        var oldStatus = task.Status;
        SetStatus(task, dto.Status, now);
        if (!isNew && oldStatus != task.Status) Record(task.Id, "status", oldStatus.ToString(), task.Status.ToString(), source);
    }

    private static void SetStatus(TaskItem task, WorkTaskStatus status, DateTime now)
    {
        task.CompletedAt = status == WorkTaskStatus.Completed ? task.CompletedAt ?? now : null;

        task.Status = status;
        task.UpdatedAt = now;
    }

    private void Record(Guid taskId, string field, string? oldValue, string? newValue, string source) =>
        db.TaskChanges.Add(new TaskChange
        {
            Id = Guid.NewGuid(),
            BusinessId = tenant.GetBusinessId(),
            TaskId = taskId,
            Field = field,
            OldValue = oldValue is { Length: > 300 } ? oldValue[..300] : oldValue,
            NewValue = newValue is { Length: > 300 } ? newValue[..300] : newValue,
            Source = source,
            At = DateTime.UtcNow,
        });

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
            t.Id, t.Title, t.Content, t.Status, t.OrderId, order?.Number, order?.Customer, t.CreatedAt, t.CompletedAt, t.IsAutomatic, order?.Status, t.Category,
            t.DueDate, t.ParentTaskId, t.UpdatedAt, t.SnoozedUntil, t.DeferCount, t.ParkedAt);
    }
}
