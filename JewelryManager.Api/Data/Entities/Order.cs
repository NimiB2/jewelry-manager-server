namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// A sale. Everything on it is a frozen snapshot: later changes to products or settings never
/// touch an existing order.
/// </summary>
public class Order
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    // Per business: test orders count up from 500, real orders from 1000.
    public int Number { get; set; }

    public DateOnly Date { get; set; }
    public string? Customer { get; set; }

    // Before discount, and after it.
    public decimal Amount { get; set; }
    public decimal FinalAmount { get; set; }
    public bool HasDiscount { get; set; }
    public string? DiscountReason { get; set; }

    public OrderSource Source { get; set; } = OrderSource.Manual;

    // The id the order has in the store it came from (Shopify), and its display name like "#1001".
    // Unique per business, so a webhook that is delivered twice cannot create the order twice.
    public string? ExternalId { get; set; }
    public string? ExternalName { get; set; }

    // Set when the store reports a change after the order was received. It is a warning only: the books
    // are never touched by the store, the owner decides. Cleared when the owner dismisses it.
    public StoreOrderAlert StoreAlert { get; set; } = StoreOrderAlert.None;
    public DateTime? StoreAlertAt { get; set; }

    // A short hash of the order as the store last sent it, to tell a real change from store noise
    // (fulfilment, tags, payment status). Null for orders that did not come from the store.
    public string? StoreFingerprint { get; set; }

    // An order from the store waits for the owner's approval before it counts (income, task).
    public bool IsPendingApproval { get; set; }

    public bool ReceiptSent { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.New;

    // The current production stage; only set while the order is in progress.
    public string? PreparationStage { get; set; }

    public string? Notes { get; set; }

    public bool IsCompleted { get; set; }
    public DateTime? CompletedDate { get; set; }

    // Soft delete: the row stays for the books, it just disappears from the app.
    public bool IsDeleted { get; set; }

    // Hourly rate at the time of the order, so profit can be worked out later without the settings.
    public decimal LaborHourRate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<OrderLineItem> Items { get; set; } = [];
}
