namespace JewelryManager.Api.Data.Entities;

/// <summary>A priced item inside a <see cref="PricingAdditionCategory"/> (e.g. a gift box under packaging).</summary>
public class PricingAdditionItem
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CategoryId { get; set; }
    public PricingAdditionCategory Category { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
}
