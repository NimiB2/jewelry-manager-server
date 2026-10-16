namespace JewelryManager.Api.Data.Entities;

public enum StatementSource
{
    Bank,
    Card,
}

/// <summary>One uploaded bank or credit card statement. The hash stops the same file being read twice.</summary>
public class StatementFile
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public StatementSource Source { get; set; }
    public string FileName { get; set; } = "";
    public string FileHash { get; set; } = "";

    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }

    // Sum of the money going out, so a card statement can be matched to the bank's "card settlement" line.
    public decimal TotalOut { get; set; }

    public DateTime UploadedAt { get; set; }
}
