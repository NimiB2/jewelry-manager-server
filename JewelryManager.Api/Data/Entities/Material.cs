namespace JewelryManager.Api.Data.Entities;

/// <summary>A raw material (silver, 14K gold...) with its cost and labor parameters per gram.</summary>
public class Material
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;
    public decimal PricePerGram { get; set; }
    public decimal LaborHoursPerGram { get; set; }

    // Markup multiplier (e.g. 1.5), not a percentage.
    public decimal ProfitMultiplier { get; set; }

    // Keeps the order the owner arranged in the UI.
    public int SortOrder { get; set; }
}
