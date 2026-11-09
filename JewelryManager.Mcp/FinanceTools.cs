using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace JewelryManager.Mcp;

/// <summary>
/// Money tools. Income is never added by hand (it follows from orders), and nothing here deletes an
/// expense, empties a list of them or merges suppliers.
/// </summary>
[McpServerToolType]
public class FinanceTools(IBackendApi api)
{
    private const string SummaryHint = "One short Hebrew sentence saying exactly what will be done, with the exact values.";

    private static readonly IReadOnlySet<string> SummaryKeys = ResultShaper.Keys(
        "summary", "totalIncome", "totalExpenses", "netProfit", "expensesWithoutReceipt");

    // Salary, work hours and the hourly rate are left out on purpose.
    private static readonly IReadOnlySet<string> AnalysisKeys = ResultShaper.Keys(
        "year", "month", "totals", "income", "expenses", "orders", "averageOrder", "fixedExpenses", "variableExpenses",
        "orderCosts", "businessExpenses", "previous", "byGroup", "byCategory", "name", "amount", "percent", "count",
        "topProducts", "topMaterials", "revenue", "quantity");

    private static readonly IReadOnlySet<string> ExpenseKeys = ResultShaper.Keys(
        "count", "total", "items", "id", "date", "description", "supplier", "typeName", "amount", "hasReceipt", "groupName", "notes");

    [McpServerTool(Name = "finance_summary", ReadOnly = true)]
    [Description("Income, expenses, net profit and the number of expenses without a receipt, for a month (yyyy-MM) or a date range.")]
    public Task<string> FinanceSummary(string? month = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var (from, to) = ToolSupport.MonthRange(month);
            var data = await api.GetAsync("/finances" + ToolSupport.Query(
                ("from", from is null ? null : ToolSupport.Iso(from.Value)),
                ("to", to is null ? null : ToolSupport.Iso(to.Value))), ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, SummaryKeys));
        });

    [McpServerTool(Name = "finance_analysis", ReadOnly = true)]
    [Description("Yearly or monthly analysis: totals, comparison to the previous period, expense breakdown, best-selling products.")]
    public Task<string> FinanceAnalysis(int year, int? month = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/finances/analysis" + ToolSupport.Query(
                ("year", year.ToString()), ("month", month?.ToString())), ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, AnalysisKeys, 5));
        });

    [McpServerTool(Name = "list_expenses", ReadOnly = true)]
    [Description("Expenses of a month (yyyy-MM), optionally only one supplier or a word in the description. Shows at most 20.")]
    public Task<string> ListExpenses(string? month = null, string? supplier = null, string? search = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var (from, to) = ToolSupport.MonthRange(month);
            var data = await api.GetAsync("/finances" + ToolSupport.Query(
                ("type", "expense"),
                ("from", from is null ? null : ToolSupport.Iso(from.Value)),
                ("to", to is null ? null : ToolSupport.Iso(to.Value))), ct);

            var rows = data.GetProperty("items").EnumerateArray()
                .Where(i => Matches(ToolSupport.Str(i, "supplier"), supplier) && Matches(ToolSupport.Str(i, "description"), search))
                .ToList();

            var shaped = JsonSerializer.SerializeToElement(new
            {
                count = rows.Count,
                total = rows.Sum(r => ToolSupport.Dec(r, "amount")),
                items = rows,
            });
            return ToolEnvelope.Ok(ResultShaper.Shape(shaped, ExpenseKeys));
        });

    [McpServerTool(Name = "list_suppliers", ReadOnly = true)]
    [Description("Names of the suppliers used so far.")]
    public Task<string> ListSuppliers(CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/expenses/suppliers", ct);
            var names = data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(n => n.GetString()).Where(n => n is not null).Take(40).ToList()
                : [];
            return ToolEnvelope.Ok(JsonSerializer.SerializeToNode(new { suppliers = names }));
        });

    [McpServerTool(Name = "expenses_without_receipt", ReadOnly = true)]
    [Description("Expenses that have no invoice attached, for a month (yyyy-MM) or all time.")]
    public Task<string> ExpensesWithoutReceipt(string? month = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var (from, to) = ToolSupport.MonthRange(month);
            var data = await api.GetAsync("/expenses/without-receipt" + ToolSupport.Query(
                ("from", from is null ? null : ToolSupport.Iso(from.Value)),
                ("to", to is null ? null : ToolSupport.Iso(to.Value))), ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, ExpenseKeys));
        });

    [McpServerTool(Name = "get_expense", ReadOnly = true)]
    [Description("Details of one expense by its id (from a list).")]
    public Task<string> GetExpense(Guid id, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync($"/expenses/{id}", ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, ExpenseKeys));
        });

    [McpServerTool(Name = "add_expense")]
    [Description("Add an expense. date defaults to today. category: VARIABLE (default) or FIXED. typeName must be one of the owner's expense types.")]
    public Task<string> AddExpense(string description, decimal amount, string? supplier = null, string? date = null,
        string? typeName = null, string? notes = null, string? category = null,
        [Description(SummaryHint)] string summary = "", CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var wantedCategory = (category ?? "VARIABLE").Trim().ToUpperInvariant();
            if (wantedCategory is not ("FIXED" or "VARIABLE")) throw new ArgumentException("category must be FIXED or VARIABLE");
            if (amount <= 0) throw new ArgumentException("amount must be above zero");

            var body = new
            {
                date = date ?? ToolSupport.Iso(ToolSupport.Today()),
                category = wantedCategory,
                description,
                amount,
                repeatEveryMonths = (int?)null,
                typeName,
                supplier,
                notes,
            };

            var created = await api.SendAsync(HttpMethod.Post, "/expenses", body, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(created, ExpenseKeys),
                new UndoCall("DELETE", $"/expenses/{ToolSupport.IdOf(created)}", null));
        });

    [McpServerTool(Name = "update_expense")]
    [Description("Change fields of one expense (by id). Only the fields you give are changed.")]
    public Task<string> UpdateExpense(Guid id, string? description = null, decimal? amount = null, string? supplier = null,
        string? date = null, string? typeName = null, string? notes = null,
        [Description(SummaryHint)] string summary = "", CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var current = await api.GetAsync($"/expenses/{id}", ct);

            var body = new
            {
                date = date ?? ToolSupport.Str(current, "date"),
                category = ToolSupport.Str(current, "category"),
                description = description ?? ToolSupport.Str(current, "description"),
                amount = amount ?? ToolSupport.Dec(current, "amount"),
                applyToSeries = false,
                typeName = typeName ?? ToolSupport.Str(current, "typeName"),
                supplier = supplier ?? ToolSupport.Str(current, "supplier"),
                notes = notes ?? ToolSupport.Str(current, "notes"),
                groupName = ToolSupport.Str(current, "groupName"),
            };

            var updated = await api.SendAsync(HttpMethod.Put, $"/expenses/{id}", body, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ExpenseKeys));
        });

    private static bool Matches(string? value, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) ||
        (value is not null && value.Contains(wanted.Trim(), StringComparison.OrdinalIgnoreCase));
}
