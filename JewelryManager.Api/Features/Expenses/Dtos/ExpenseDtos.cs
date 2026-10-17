using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Expenses.Dtos;

/// <summary>
/// Body of POST /expenses. RepeatEveryMonths turns it into the first row of a recurring series
/// (a fixed expense: the same amount again every N months).
/// </summary>
public record CreateExpenseDto(
    DateOnly Date,
    ExpenseCategory Category,
    [Required, MaxLength(300)] string Description,
    [Range(0.01, 100_000_000)] decimal Amount,
    [Range(1, 24)] int? RepeatEveryMonths,
    // One of the names from her expense types list.
    [MaxLength(100)] string? TypeName = null,
    // The order this expense was made for (optional).
    Guid? OrderId = null,
    [MaxLength(100)] string? Supplier = null,
    [MaxLength(1000)] string? Notes = null,
    // One of the names from her expense groups list.
    [MaxLength(100)] string? GroupName = null);

/// <summary>Body of PUT /expenses/{id}. ApplyToSeries also changes the series template and its later occurrences.</summary>
public record UpdateExpenseDto(
    DateOnly Date,
    ExpenseCategory Category,
    [Required, MaxLength(300)] string Description,
    [Range(0.01, 100_000_000)] decimal Amount,
    bool ApplyToSeries,
    [MaxLength(100)] string? TypeName = null,
    Guid? OrderId = null,
    [MaxLength(100)] string? Supplier = null,
    [MaxLength(1000)] string? Notes = null,
    [MaxLength(100)] string? GroupName = null);

/// <summary>Body of POST /expenses/suppliers/merge: every supplier in Names becomes Target.</summary>
public record MergeSuppliersDto(
    [Required, MaxLength(100)] string Target,
    [Required] List<string> Names);

/// <summary>What a delete of a recurring expense removes.</summary>
public enum ExpenseDeleteScope
{
    // Only this occurrence; the series goes on.
    This,

    // This one and every later one, and the series stops.
    FromHere,

    // Every later one only, and the series stops; this one and the earlier ones stay.
    After,
}

public record ExpenseResponse(
    Guid Id,
    DateOnly Date,
    ExpenseCategory Category,
    string Description,
    decimal Amount,
    bool HasReceipt,
    Guid? SeriesId,
    int? RepeatEveryMonths,
    string? TypeName,
    Guid? OrderId,
    int? OrderNumber,
    string? InvoiceFileName,
    string? Supplier,
    string? Notes,
    string? GroupName);

/// <summary>One expense that has no invoice, as shown before a bulk delete.</summary>
public record WithoutReceiptItem(
    Guid Id,
    DateOnly Date,
    string Description,
    string? Supplier,
    string? TypeName,
    decimal Amount,
    bool IsRecurring,
    bool LinkedToOrder,
    bool FromStatement);

public record WithoutReceiptList(List<WithoutReceiptItem> Items, decimal Total);

/// <summary>
/// Body of POST /expenses/without-receipt/delete. ExpectedCount is what she saw in the preview: if the books
/// changed since, nothing is deleted and she looks again.
/// </summary>
public record DeleteWithoutReceiptDto(DateOnly? From, DateOnly? To, bool IncludeRecurring, [Range(1, 100_000)] int ExpectedCount);
