namespace JewelryManager.Api.Data.Entities;

/// <summary>A user-defined cost category (packaging, shipping...) with a base price plus optional items.</summary>
public class PricingAdditionCategory
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public int SortOrder { get; set; }

    public ICollection<PricingAdditionItem> Items { get; set; } = [];
}
