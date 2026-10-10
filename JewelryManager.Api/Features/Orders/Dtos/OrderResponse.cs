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
    string? Note,
    // The store's product id for a line that came from the store (null for manual lines).
    string? ExternalProductId = null,
    // True on a pending order's line that still has to be tied to a catalog product.
    bool NeedsProduct = false);

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
    List<OrderItemResponse> Items,
    // The store's display name ("#1001") and whether the owner still has to approve the order.
    string? ExternalName = null,
    bool IsPendingApproval = false);

/// <summary>Totals over exactly the orders the list is showing (e.g. one month).</summary>
public record OrdersSummary(int Count, decimal TotalFinalAmount);

public record OrdersListResponse(List<OrderResponse> Orders, OrdersSummary Summary);
