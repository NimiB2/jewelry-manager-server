namespace JewelryManager.Api.Data.Entities;

/// <summary>A quick-pick discount percentage (5, 10, 15...) shown as a button in the product list simulator.</summary>
public class DiscountPreset
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public decimal Percent { get; set; }
    public int SortOrder { get; set; }
}
