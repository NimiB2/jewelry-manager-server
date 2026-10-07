namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// Money coming in. Income from an order is created when the order is completed and is a frozen
/// snapshot; it is read-only here and changes only by reopening the order.
/// </summary>
public class Income
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public DateOnly Date { get; set; }
    public IncomeCategory Category { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }

    // Frozen at completion, so profit per hour of work can be worked out without the settings.
    public decimal? WorkHours { get; set; }
    public decimal? LaborHourRate { get; set; }

    // Null for income entered by hand.
    public Guid? OrderId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
