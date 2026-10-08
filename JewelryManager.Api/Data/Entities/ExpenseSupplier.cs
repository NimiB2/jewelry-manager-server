namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// A supplier on her list. A supplier typed into an expense joins the list by itself (at the top,
/// ready for the next expense); she can reorder or remove it in her settings.
/// </summary>
public class ExpenseSupplier
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;

    // A new supplier goes in front of the others, so it can be a negative number.
    public int SortOrder { get; set; }
}
