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
