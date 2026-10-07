namespace JewelryManager.Api.Data.Entities;

/// <summary>Junction row: the database itself guarantees a product never points at a missing collection.</summary>
public class ProductCollection
{
    public Guid BusinessId { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;
}
