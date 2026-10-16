using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Statements.Dtos;

namespace JewelryManager.Api.Features.Statements;

/// <summary>
/// The rules for telling whether a statement line is an expense she already entered. A close date alone
/// is a weak sign; the same amount together with the supplier's name is a strong one, even a few weeks apart
/// (a monthly payment is often entered on a different day than the bank charges it).
/// </summary>
public static class StatementMatching
{
    // Date gap allowed for any same-amount candidate, and for one whose name also matches.
    public const int LooseWindowDays = 5;
    public const int NamedWindowDays = 20;
    private const int StrongWindowDays = 2;

    public static string KeyOf(string text) => ExpensesService.SupplierKey(text);

    public static bool NameMatches(string lineKey, Expense expense) =>
        KeysOverlap(lineKey, KeyOf(expense.Supplier ?? "")) || KeysOverlap(lineKey, KeyOf(expense.Description));

    public static bool KeysOverlap(string a, string b) =>
        a.Length >= 3 && b.Length >= 3 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    public static bool SameAmount(StatementLine line, Expense expense) => Math.Abs(expense.Amount - line.Amount) < 0.005m;

    public static int DaysApart(StatementLine line, Expense expense) => Math.Abs(expense.Date.DayNumber - line.Date.DayNumber);

    /// <summary>Same amount, and either a close date or the same name within a few weeks.</summary>
    public static bool IsCandidate(StatementLine line, Expense expense, string lineKey)
    {
        if (line.Amount <= 0 || !SameAmount(line, expense)) return false;
        var days = DaysApart(line, expense);
        return days <= LooseWindowDays || (days <= NamedWindowDays && NameMatches(lineKey, expense));
    }

    public static List<MatchSuggestion> SuggestionsFor(StatementLine line, IEnumerable<Expense> pool)
    {
        var lineKey = KeyOf(line.Description);
        return pool
            .Where(e => IsCandidate(line, e, lineKey))
            .Select(e =>
            {
                var days = DaysApart(line, e);
                var strong = days <= StrongWindowDays || NameMatches(lineKey, e);
                return (Days: days, Suggestion: new MatchSuggestion(e.Id, e.Date, e.Description, e.Supplier, e.Amount, strong));
            })
            .OrderByDescending(x => x.Suggestion.Strong).ThenBy(x => x.Days)
            .Take(3)
            .Select(x => x.Suggestion)
            .ToList();
    }
}
