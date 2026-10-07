namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// A catalog product in its lean form: only what the owner typed in. Cost and price are
/// never stored — they are recomputed from the current settings every time.
/// </summary>
public class Product
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // The material's name in Settings (materials are replaced as a whole when edited, so no FK).
    public string Material { get; set; } = string.Empty;

    public decimal Weight { get; set; }
    public decimal AdditionalWorkHours { get; set; }

    // The price the owner actually charges; the recommended price is computed separately.
    public decimal SitePrice { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ProductCollection> Collections { get; set; } = [];
    public ICollection<ProductAddition> Additions { get; set; } = [];
}
