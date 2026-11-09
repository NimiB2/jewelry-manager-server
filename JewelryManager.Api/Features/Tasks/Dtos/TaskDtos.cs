using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Tasks.Dtos;

/// <summary>
/// Body of POST /tasks and PUT /tasks/{id}. OrderId is optional: a task can stand alone.
/// DueDate and ParentTaskId left empty on an update keep what the task already has.
/// </summary>
public record SaveTaskDto(
    [Required, MaxLength(200)] string Title,
    [MaxLength(4_000)] string? Content,
    WorkTaskStatus Status,
    Guid? OrderId,
    [MaxLength(100)] string? Category = null,
    DateOnly? DueDate = null,
    Guid? ParentTaskId = null);

public record UpdateTaskStatusDto(WorkTaskStatus Status);

/// <summary>Body of PATCH /tasks/{id}/coach: snooze, park, unpark or clear-due.</summary>
public record TaskCoachActionDto([Required, MaxLength(20)] string Action, DateTime? Until = null);

/// <summary>Body of POST /tasks/batch: the small steps of a breakdown, or a checklist, created together.</summary>
public record BatchTasksDto([Required, MinLength(1), MaxLength(12)] List<SaveTaskDto> Tasks);

/// <summary>Body of POST /tasks/batch-delete: only used to take back a batch the agent just created.</summary>
public record BatchDeleteTasksDto([Required, MinLength(1), MaxLength(12)] List<Guid> Ids);

public record TaskResponse(
    Guid Id,
    string Title,
    string? Content,
    WorkTaskStatus Status,
    Guid? OrderId,
    int? OrderNumber,
    string? OrderCustomer,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    // Created by the system for a new order.
    bool IsAutomatic,
    // The status of the linked order. For an automatic task this is the status to show: the order is the source.
    OrderStatus? OrderStatus,
    string? Category,
    DateOnly? DueDate = null,
    Guid? ParentTaskId = null,
    DateTime? UpdatedAt = null,
    DateTime? SnoozedUntil = null,
    int DeferCount = 0,
    DateTime? ParkedAt = null);
