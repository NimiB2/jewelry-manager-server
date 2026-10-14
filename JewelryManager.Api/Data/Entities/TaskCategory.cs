namespace JewelryManager.Api.Data.Entities;

/// <summary>A category for general tasks on her list (marketing, materials...), kept in her settings. Stored by name on the task.</summary>
public class TaskCategory
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
