namespace JewelryManager.Api.Data.Entities;

/// <summary>A production stage an in-progress order moves through (casting, polishing...).</summary>
public class PreparationStage
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;

    // The order of the stages is meaningful, so it is stored explicitly.
    public int SortOrder { get; set; }
}
