namespace JewelryManager.Api.Data.Entities;

/// <summary>A to-do. Named TaskItem to avoid clashing with System.Threading.Tasks.Task.</summary>
public class TaskItem
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.New;

    // Optional: a task can stand alone or hang on an order.
    public Guid? OrderId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
