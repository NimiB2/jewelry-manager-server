using System.Text.RegularExpressions;

namespace JewelryManager.Api.Features.Agent.Coach;

public enum IndexAction
{
    None,
    MarkCrossing,
    ClearCrossing,
    Alert,
}

/// <summary>
/// The arithmetic behind the metal price index. The index is the market price in shekels per gram; it is only
/// ever compared with itself (today against the day her prices were last true), because her supplier's price
/// also carries a premium that is not ours to guess.
/// </summary>
public static partial class MetalMath
{
    public const decimal GramsPerTroyOunce = 31.1034768m;

    public static decimal IlsPerGram(decimal usdPerOunce, decimal usdIls) =>
        Math.Round(usdPerOunce * usdIls / GramsPerTroyOunce, 4);

    /// <summary>Two sources agree when they differ by no more than the tolerance (percent of their mean). Returns the mean.</summary>
    public static decimal? CrossCheck(decimal a, decimal b, decimal tolerancePercent)
    {
        if (a <= 0 || b <= 0) return null;
        var mean = (a + b) / 2;
        return Math.Abs(a - b) / mean * 100 <= tolerancePercent ? Math.Round(mean, 4) : null;
    }

    /// <summary>With more than two sources, the mean of the largest group that agree with each other (at least two).</summary>
    public static decimal? Consensus(IReadOnlyList<decimal> values, decimal tolerancePercent)
    {
        decimal? best = null;
        var bestSize = 1;
        for (var i = 0; i < values.Count; i++)
        {
            var group = values.Where(v => v > 0 && values[i] > 0 && Math.Abs(v - values[i]) / values[i] * 100 <= tolerancePercent).ToList();
            if (group.Count > bestSize)
            {
                bestSize = group.Count;
                best = Math.Round(group.Average(), 4);
            }
        }

        return best;
    }

    public static decimal ChangePercent(decimal current, decimal baseline) =>
        baseline <= 0 ? 0 : Math.Round((current / baseline - 1) * 100, 2);

    /// <summary>How far above the market her price per gram sits for this purity (1.10 means 10% above).</summary>
    public static decimal Premium(decimal settingsPricePerGram, decimal spotIlsPerGram, decimal purity) =>
        spotIlsPerGram <= 0 || purity <= 0 ? 0 : Math.Round(settingsPricePerGram / (spotIlsPerGram * purity), 4);

    /// <summary>
    /// An alert needs the threshold crossed on two different days, is sent once per crossing, and the crossing
    /// is forgotten only when the index comes well back (under 80% of the threshold).
    /// </summary>
    public static IndexAction Evaluate(decimal changePercent, decimal threshold, DateOnly? firstCrossedOn, DateOnly? alertedOn, DateOnly today)
    {
        var size = Math.Abs(changePercent);
        if (size < threshold * 0.8m) return firstCrossedOn is null && alertedOn is null ? IndexAction.None : IndexAction.ClearCrossing;
        if (size < threshold || alertedOn is not null) return IndexAction.None;
        if (firstCrossedOn is null) return IndexAction.MarkCrossing;
        return today > firstCrossedOn ? IndexAction.Alert : IndexAction.None;
    }

    /// <summary>Works out the metal and purity from a material's name ("זהב 14K", "כסף 925"). Null when it is not clear.</summary>
    public static (string Metal, decimal Purity)? Guess(string materialName)
    {
        var name = materialName.ToLowerInvariant();

        if (name.Contains("זהב") || name.Contains("gold"))
        {
            var match = Karat().Match(name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var karat)) return null;
            return karat switch
            {
                9 => ("gold", 0.375m),
                10 => ("gold", 0.417m),
                14 => ("gold", 0.585m),
                18 => ("gold", 0.75m),
                21 => ("gold", 0.875m),
                22 => ("gold", 0.916m),
                24 => ("gold", 0.999m),
                _ => null,
            };
        }

        if (name.Contains("כסף") || name.Contains("silver") || name.Contains("925"))
            return ("silver", name.Contains("999") ? 0.999m : name.Contains("950") ? 0.95m : 0.925m);

        return null;
    }

    [GeneratedRegex(@"(\d{1,2})\s*(k|kt|ק|קראט|קרט)?")]
    private static partial Regex Karat();

    public static string MetalName(string metal) => metal == "gold" ? "הזהב" : "הכסף";
}
