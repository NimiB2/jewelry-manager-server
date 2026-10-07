namespace JewelryManager.Api.Data.Entities;

/// <summary>An option the owner can add to a product (stone, setting, plating, "other"...). Managed in personal settings.</summary>
public class ProductAdditionType
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;

    // "Other"-style types let the owner type any name when adding them to a product.
    public bool AllowsCustomName { get; set; }

    public int SortOrder { get; set; }
}
