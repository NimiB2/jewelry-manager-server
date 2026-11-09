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

    // One of the names from her task categories list (optional).
    public string? Category { get; set; }

    // Created and kept in step by the system when an order is added (it follows the order's status and goes with it).
    public bool IsAutomatic { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // A date she gave it. Moving it later counts as a postponement.
    public DateOnly? DueDate { get; set; }

    // A small step of a bigger task (from a breakdown); null for a normal task.
    public Guid? ParentTaskId { get; set; }

    // The agent leaves the task alone until this time.
    public DateTime? SnoozedUntil { get; set; }

    // How many times it was postponed.
    public int DeferCount { get; set; }

    // Set aside by her decision: never nagged, still listed on request.
    public DateTime? ParkedAt { get; set; }
}
