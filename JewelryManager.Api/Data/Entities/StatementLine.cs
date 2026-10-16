namespace JewelryManager.Api.Data.Entities;

public enum StatementLineStatus
{
    // Needs a decision: match to an existing expense, create one, or ignore.
    Pending,

    // Linked to an expense she had already entered.
    Matched,

    // An expense was created from this line.
    Created,

    // Not an expense (income, own transfer, refund...).
    Ignored,

    // The bank's lump-sum charge for a credit card. The real expenses are in the card statement.
    CardSettlement,
}

/// <summary>
/// One row of a statement. It is never an expense by itself: it becomes one only through an explicit
/// match or create, which is what keeps the books free of doubles and of gaps.
/// </summary>
public class StatementLine
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid FileId { get; set; }

    public StatementSource Source { get; set; }
    public DateOnly Date { get; set; }

    // Money going out is positive, money coming in is negative.
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public string? Reference { get; set; }

    // Identifies the row across overlapping statements, so a re-upload never adds it twice.
    public string Fingerprint { get; set; } = "";

    public StatementLineStatus Status { get; set; }

    // Set while Matched or Created. One expense belongs to at most one line.
    public Guid? ExpenseId { get; set; }

    // For a CardSettlement: the card statement that covers it.
    public Guid? CoveredByFileId { get; set; }

    public DateTime CreatedAt { get; set; }
}
