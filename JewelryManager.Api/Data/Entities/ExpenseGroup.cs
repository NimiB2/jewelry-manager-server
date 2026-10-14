namespace JewelryManager.Api.Data.Entities;

/// <summary>A main group of expenses on her list (orders, business, home...), kept in her settings. Stored by name on the expense.</summary>
public class ExpenseGroup
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
