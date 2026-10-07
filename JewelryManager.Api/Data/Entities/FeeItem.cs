namespace JewelryManager.Api.Data.Entities;

/// <summary>A percentage fee (card fee, VAT, fixed costs...) that feeds the pricing formula.</summary>
public class FeeItem
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;
    public decimal Percent { get; set; }

    // Permanent fees can't be deleted from the UI: the pricing formula relies on them.
    public bool IsPermanent { get; set; }

    public int SortOrder { get; set; }
}
