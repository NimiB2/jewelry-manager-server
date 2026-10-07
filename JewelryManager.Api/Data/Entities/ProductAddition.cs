namespace JewelryManager.Api.Data.Entities;

/// <summary>One priced extra on a product (a stone, setting, plating...).</summary>
public class ProductAddition
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    // The addition type's name at the time of saving (types can be renamed in Settings).
    public string TypeName { get; set; } = string.Empty;

    // Only filled for types that allow free text (the "other" type).
    public string? CustomName { get; set; }

    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public int SortOrder { get; set; }
}
