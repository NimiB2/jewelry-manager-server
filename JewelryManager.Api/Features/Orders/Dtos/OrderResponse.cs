using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Orders.Dtos;

public record OrderItemResponse(
    Guid Id,
    Guid? ProductId,
    string Name,
    string Type,
    string Material,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    decimal WorkHours,
    string? Note);

public record OrderResponse(
    Guid Id,
    int Number,
    bool IsTest,
    DateOnly Date,
    string? Customer,
    decimal Amount,
    decimal FinalAmount,
    bool HasDiscount,
    decimal DiscountAmount,
    decimal DiscountPercent,
    string? DiscountReason,
    OrderSource Source,
    bool ReceiptSent,
    OrderStatus Status,
    string? PreparationStage,
    string? Notes,
    bool IsCompleted,
    DateTime? CompletedDate,
    List<OrderItemResponse> Items);

/// <summary>Totals over exactly the orders the list is showing (e.g. one month).</summary>
public record OrdersSummary(int Count, decimal TotalFinalAmount);

public record OrdersListResponse(List<OrderResponse> Orders, OrdersSummary Summary);
