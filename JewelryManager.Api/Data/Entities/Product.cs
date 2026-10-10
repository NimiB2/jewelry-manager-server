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

    // The product's name in the Shopify store (often English), used to match incoming orders.
    public string? ShopifyName { get; set; }

    // Shopify's own id for the product; learned when an order line is linked, never typed by hand.
    public string? ShopifyProductId { get; set; }

    // What the store offers for this product (one entry per variant: size, price...), kept as JSON for display only.
    public string? ShopifyVariants { get; set; }

    // A product imported from the store has only a name and a price; type and material come later.
    public bool NeedsDetails => string.IsNullOrWhiteSpace(Type) || string.IsNullOrWhiteSpace(Material);

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ProductCollection> Collections { get; set; } = [];
    public ICollection<ProductAddition> Additions { get; set; } = [];
}
