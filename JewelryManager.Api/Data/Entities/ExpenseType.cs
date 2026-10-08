namespace JewelryManager.Api.Data.Entities;

/// <summary>A kind of expense the owner picks from a list (materials, packaging, marketing...), kept in her settings.</summary>
public class ExpenseType
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
