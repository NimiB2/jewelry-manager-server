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
    bool IsPendingApproval = false,
    // What the store reported after the order arrived (cancelled, deleted, changed). A warning only.
    StoreOrderAlert StoreAlert = StoreOrderAlert.None,
    DateTime? StoreAlertAt = null,
    // For a store order: the customer's phone, the same number as a WhatsApp link needs it (digits, with the
    // country code), the order-summary message ready to send her, and when it was marked as sent.
    string? CustomerPhone = null,
    string? CustomerWhatsApp = null,
    string? CustomerMessage = null,
    DateTime? CustomerNotifiedAt = null);

/// <summary>
/// What needs the owner's attention: orders waiting for approval, orders the store changed, and store
/// orders whose summary message still has to be sent. Total counts each order once.
/// </summary>
public record OrdersAttentionResponse(int Pending, int Alerts, int MessagesToSend, int Total);

/// <summary>Totals over exactly the orders the list is showing (e.g. one month).</summary>
public record OrdersSummary(int Count, decimal TotalFinalAmount);

public record OrdersListResponse(List<OrderResponse> Orders, OrdersSummary Summary);
