using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Expenses.Dtos;

/// <summary>Body of POST /expenses. RepeatEveryMonths turns it into the first row of a recurring series.</summary>
public record CreateExpenseDto(
    DateOnly Date,
    ExpenseCategory Category,
    [Required, MaxLength(300)] string Description,
    [Range(0.01, 100_000_000)] decimal Amount,
    bool HasReceipt,
    [Range(1, 24)] int? RepeatEveryMonths);

/// <summary>Body of PUT /expenses/{id}. ApplyToSeries also changes the series template and its later occurrences.</summary>
public record UpdateExpenseDto(
    DateOnly Date,
    ExpenseCategory Category,
    [Required, MaxLength(300)] string Description,
    [Range(0.01, 100_000_000)] decimal Amount,
    bool HasReceipt,
    bool ApplyToSeries);

public record ExpenseResponse(
    Guid Id,
    DateOnly Date,
    ExpenseCategory Category,
    string Description,
    decimal Amount,
    bool HasReceipt,
    Guid? SeriesId,
    int? RepeatEveryMonths);
