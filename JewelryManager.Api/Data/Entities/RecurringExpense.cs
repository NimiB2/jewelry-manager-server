namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// The template of a recurring expense. Occurrences are created lazily: every row whose
/// <see cref="NextDate"/> has arrived is generated, so a deleted occurrence is never regenerated.
/// </summary>
public class RecurringExpense
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public ExpenseCategory Category { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }

    public int EveryMonths { get; set; }

    // The date of the next occurrence that was not generated yet.
    public DateOnly NextDate { get; set; }

    // The day of month the series started on, so Jan 31 -> Feb 28 -> Mar 31 and doesn't drift to the 28th.
    public int DayOfMonth { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
