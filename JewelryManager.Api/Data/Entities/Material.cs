namespace JewelryManager.Api.Data.Entities;

/// <summary>A raw material (silver, 14K gold...) with its price per gram and fixed labor hours per piece.</summary>
public class Material
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;
    public decimal PricePerGram { get; set; }
    // Fixed work hours for one piece in this material (e.g. 1 for silver, 2 for gold), regardless of weight.
    public decimal LaborHours { get; set; }

    // Markup multiplier (e.g. 1.5), not a percentage.
    public decimal ProfitMultiplier { get; set; }

    // Keeps the order the owner arranged in the UI.
    public int SortOrder { get; set; }
}
