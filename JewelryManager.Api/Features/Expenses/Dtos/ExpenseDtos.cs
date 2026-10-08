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
    [MaxLength(1000)] string? Notes = null);

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
    [MaxLength(1000)] string? Notes = null);

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
    string? Notes);
