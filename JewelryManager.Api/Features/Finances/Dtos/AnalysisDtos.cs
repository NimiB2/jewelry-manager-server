namespace JewelryManager.Api.Features.Finances.Dtos;

/// <summary>One month of the year: money in and out, and what the work hours of its orders were worth.</summary>
public record AnalysisMonth(
    int Month,
    decimal Income,
    decimal Expenses,
    // Work hours of the month's orders x the hourly rate that was frozen on each order.
    decimal Salary,
    decimal WorkHours,
    int Orders);

/// <summary>One slice of the expense breakdown (a main group or a category).</summary>
public record AnalysisSlice(string Name, decimal Amount, decimal Percent, int Count, decimal PreviousAmount);

/// <summary>A category inside one main group, so choosing a group can show only its own categories.</summary>
public record AnalysisPair(string Group, string Category, decimal Amount, int Count, decimal PreviousAmount);

/// <summary>A product or material ranked by what it brought in.</summary>
public record AnalysisTopItem(string Name, decimal Revenue, int Quantity);

public record AnalysisTotals(
    decimal Income,
    decimal Expenses,
    decimal Salary,
    decimal WorkHours,
    int Orders,
    decimal AverageOrder,
    // Income per work hour; null when no hours were recorded.
    decimal? IncomePerHour,
    decimal FixedExpenses,
    decimal VariableExpenses);

public record FinanceAnalysisResponse(
    int Year,
    int? Month,
    List<AnalysisMonth> Months,
    AnalysisTotals Totals,
    List<AnalysisSlice> ByGroup,
    List<AnalysisSlice> ByCategory,
    List<AnalysisPair> ByGroupCategory,
    List<AnalysisTopItem> TopProducts,
    List<AnalysisTopItem> TopMaterials);
