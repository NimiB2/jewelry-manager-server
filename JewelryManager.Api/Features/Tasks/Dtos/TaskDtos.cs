using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Tasks.Dtos;

/// <summary>Body of POST /tasks and PUT /tasks/{id}. OrderId is optional: a task can stand alone.</summary>
public record SaveTaskDto(
    [Required, MaxLength(200)] string Title,
    [MaxLength(4_000)] string? Content,
    WorkTaskStatus Status,
    Guid? OrderId);

public record UpdateTaskStatusDto(WorkTaskStatus Status);

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
    OrderStatus? OrderStatus);
