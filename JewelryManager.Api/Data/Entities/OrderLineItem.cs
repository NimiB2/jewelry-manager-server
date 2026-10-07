namespace JewelryManager.Api.Data.Entities;

/// <summary>One line of an order: a frozen copy of the catalog product at the time of the order.</summary>
public class OrderLineItem
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    // Link back to the catalog; cleared (not cascaded) if the product is later deleted.
    public Guid? ProductId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;

    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; } = 1;

    // Work hours for one piece when the order was made, for the profit calculation later.
    public decimal WorkHours { get; set; }

    public int SortOrder { get; set; }
}
