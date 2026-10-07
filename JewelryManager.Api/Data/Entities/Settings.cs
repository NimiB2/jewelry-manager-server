namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// One row per business: the scalar pricing settings. List-like settings (materials, fees,
/// packaging, stages) live in their own tables.
/// </summary>
public class Settings
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Business Business { get; set; } = null!;

    public decimal LaborHourRate { get; set; }
    public decimal ProfitFloorPercent { get; set; }

    // A customer name starting with this marks a test order (numbered from 500 instead of 1000).
    public string TestOrderPrefix { get; set; } = "בדיקה";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
