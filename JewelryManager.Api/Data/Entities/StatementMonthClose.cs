namespace JewelryManager.Api.Data.Entities;

/// <summary>A month she has finished going through. Month is "yyyy-MM".</summary>
public class StatementMonthClose
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string Month { get; set; } = "";
    public DateTime ClosedAt { get; set; }
}
