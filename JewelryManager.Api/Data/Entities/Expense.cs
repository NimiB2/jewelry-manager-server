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

    // The expense type picked from her settings list (materials, packaging...). Stored by name, like the preparation stages.
    public string? TypeName { get; set; }

    // Who it was bought from (optional, free text).
    public string? Supplier { get; set; }

    // A free note on this expense (optional).
    public string? Notes { get; set; }

    // Optional link to the order this expense was made for.
    public Guid? OrderId { get; set; }

    // There is an invoice for this expense: ticked by hand, or implied by an attached file.
    public bool HasReceipt { get; set; }

    // The attached invoice file. StoredName is the generated name inside private storage; the original name is for display and download.
    public string? InvoiceStoredName { get; set; }
    public string? InvoiceFileName { get; set; }
    public string? InvoiceContentType { get; set; }

    // Set when the row belongs to a recurring series.
    public Guid? SeriesId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
