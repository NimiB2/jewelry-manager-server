namespace JewelryManager.Api.Data.Entities;

/// <summary>Money going out. A recurring expense is a row generated from a <see cref="RecurringExpense"/> series.</summary>
public class Expense
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public DateOnly Date { get; set; }
    public ExpenseCategory Category { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }

    // Marked by hand for now; the receipt upload (extension 4.2) will set it automatically.
    public bool HasReceipt { get; set; }

    // Set when the row belongs to a recurring series.
    public Guid? SeriesId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
