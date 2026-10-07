namespace JewelryManager.Api.Data.Entities;

public class Collection
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Business Business { get; set; } = null!;  // null! = "EF Core will always populate this"

    public string Name { get; set; } = string.Empty;
    public bool IsPermanent { get; set; }

    // Stable internal key for permanent collections (e.g. "general").
    // Independent of the display name, which owners can rename.
    public string? Key { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Join table navigation — EF Core resolves the many-to-many through ProductCollection.
    // Added later when the Products feature is built.
    // public ICollection<ProductCollection> Products { get; set; } = [];
}
