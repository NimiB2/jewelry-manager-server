using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Statements.Dtos;

namespace JewelryManager.Api.Features.Statements;

/// <summary>
/// What earlier decisions say about a line: the category, group and supplier she gave the same payee before,
/// and whether the same charge keeps coming month after month. Lets her confirm a known expense in one tap.
/// </summary>
public class StatementMemory(
    List<(string Key, Expense Expense)> decided,
    List<(string Key, DateOnly Date, decimal Amount)> history)
{
    public LearnedDefaults? LearnedFor(StatementLine line)
    {
        var lineKey = StatementMatching.KeyOf(line.Description);
        if (lineKey.Length < 3) return null;

        // The very same payee text first; otherwise a supplier whose name is part of the text.
        var matches = decided.Where(d => d.Key == lineKey && d.Expense.TypeName != null).Select(d => d.Expense).ToList();
        if (matches.Count == 0)
            matches = decided.Where(d => d.Expense.TypeName != null && StatementMatching.KeysOverlap(lineKey, d.Key)).Select(d => d.Expense).ToList();
        if (matches.Count == 0) return null;

        var best = matches
            .GroupBy(e => (e.TypeName, e.GroupName, e.Supplier, e.Description))
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(e => e.Date))
            .First().Key;
        return new LearnedDefaults(best.Description, best.TypeName, best.GroupName, best.Supplier);
    }

    // The same payee and amount in another month, or an expense of that payee that already repeats by itself.
    public bool LooksRecurring(StatementLine line)
    {
        var lineKey = StatementMatching.KeyOf(line.Description);
        if (lineKey.Length < 3) return false;

        var thisMonth = (line.Date.Year, line.Date.Month);
        return history.Any(h => h.Key == lineKey && h.Amount == line.Amount && (h.Date.Year, h.Date.Month) != thisMonth)
            || decided.Any(d => d.Expense.SeriesId != null && StatementMatching.KeysOverlap(lineKey, d.Key));
    }
}
