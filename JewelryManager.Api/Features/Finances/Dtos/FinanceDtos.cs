using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Finances.Dtos;

public enum FinanceKind
{
    Income,
    Expense,
}

/// <summary>One row of the mixed list. Exactly one of the two categories is set, matching Kind.</summary>
public record FinanceItem(
    FinanceKind Kind,
    Guid Id,
    DateOnly Date,
    string Description,
    decimal Amount,
    ExpenseCategory? ExpenseCategory,
    IncomeCategory? IncomeCategory,
    // Income that came from an order: read-only, edited through the order.
    Guid? OrderId,
    bool HasReceipt,
    // Set for recurring expenses.
    Guid? SeriesId,
    int? RepeatEveryMonths);

/// <summary>Totals for the whole period, regardless of the list filters.</summary>
public record FinanceSummary(decimal TotalIncome, decimal TotalExpenses, decimal NetProfit, int ExpensesWithoutReceipt);

public record FinanceListResponse(List<FinanceItem> Items, FinanceSummary Summary);
