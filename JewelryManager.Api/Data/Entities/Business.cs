namespace JewelryManager.Api.Data.Entities;

public class Business
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Stored as JSON strings — PostgreSQL JSONB columns.
    // We use string here and deserialize in the service when needed.
    public string? Branding { get; set; }
    public string? ShopifySettings { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties — EF Core uses these to understand relationships.
    // ICollection means "one Business has many Users, Products, etc."
    public ICollection<User> Users { get; set; } = [];
    public ICollection<Collection> Collections { get; set; } = [];
}
