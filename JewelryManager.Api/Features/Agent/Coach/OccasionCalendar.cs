using System.Globalization;

namespace JewelryManager.Api.Features.Agent.Coach;

public record Occasion(string Code, string Name);

/// <summary>
/// Dates of the occasions that matter to a jewelry business, computed from the Hebrew and Gregorian calendars
/// so they are right every year. Which of them matter to HER is learned from her own orders (see <see cref="Lift"/>).
/// </summary>
public static class OccasionCalendar
{
    public static readonly IReadOnlyList<Occasion> All =
    [
        new("valentine", "יום האהבה"),
        new("family_day", "יום המשפחה"),
        new("passover", "פסח"),
        new("tu_bav", "ט\"ו באב"),
        new("rosh_hashana", "ראש השנה"),
        new("hanukkah", "חנוכה"),
        new("black_friday", "בלאק פריידיי"),
    ];

    public static Occasion? Find(string code) => All.FirstOrDefault(o => o.Code == code);

    // The Hebrew calendar is the only reason to hold a calendar object: its months shift in leap years.
    private static readonly HebrewCalendar Hebrew = new();

    /// <summary>Every date the occasion falls on in the given Gregorian years (inclusive).</summary>
    public static List<DateOnly> Occurrences(string code, int fromYear, int toYear)
    {
        var dates = new List<DateOnly>();
        for (var year = fromYear; year <= toYear; year++)
        {
            switch (code)
            {
                case "valentine":
                    dates.Add(new DateOnly(year, 2, 14));
                    break;
                case "black_friday":
                    dates.Add(BlackFriday(year));
                    break;
                default:
                    dates.AddRange(HebrewDatesIn(code, year));
                    break;
            }
        }

        return dates.Distinct().OrderBy(d => d).ToList();
    }

    /// <summary>The first occurrence on or after the given day.</summary>
    public static DateOnly NextOnOrAfter(string code, DateOnly from) =>
        Occurrences(code, from.Year, from.Year + 2).First(d => d >= from);

    /// <summary>The fourth Thursday of November is Thanksgiving; Black Friday is the day after.</summary>
    public static DateOnly BlackFriday(int year)
    {
        var day = new DateOnly(year, 11, 1);
        var thursdays = 0;
        while (true)
        {
            if (day.DayOfWeek == DayOfWeek.Thursday && ++thursdays == 4) return day.AddDays(1);
            day = day.AddDays(1);
        }
    }

    private static IEnumerable<DateOnly> HebrewDatesIn(string code, int gregorianYear)
    {
        var startYear = Hebrew.GetYear(new DateTime(gregorianYear, 1, 1));
        for (var hebrewYear = startYear; hebrewYear <= startYear + 1; hebrewYear++)
        {
            var (month, day) = code switch
            {
                "rosh_hashana" => (1, 1),
                "hanukkah" => (3, 25),
                "family_day" => (5, 30),
                "passover" => (IsLeap(hebrewYear) ? 8 : 7, 15),
                "tu_bav" => (IsLeap(hebrewYear) ? 12 : 11, 15),
                _ => (0, 0),
            };
            if (month == 0) continue;

            var date = DateOnly.FromDateTime(Hebrew.ToDateTime(hebrewYear, month, day, 0, 0, 0, 0));
            if (date.Year == gregorianYear) yield return date;
        }
    }

    private static bool IsLeap(int hebrewYear) => Hebrew.IsLeapYear(hebrewYear);

    /// <summary>
    /// How much busier the three weeks before the occasion were, compared with an ordinary three weeks,
    /// averaged over past years. Null when there is too little history to say (under six orders).
    /// </summary>
    public static double? Lift(string code, IReadOnlyList<(DateOnly Date, int Orders)> days, DateOnly today)
    {
        if (days.Count == 0) return null;
        var first = days.Min(d => d.Date);
        var totalOrders = days.Sum(d => d.Orders);
        var spanWindows = (today.DayNumber - first.DayNumber) / 21.0;
        if (totalOrders < 6 || spanWindows < 2) return null;

        var occurrences = Occurrences(code, today.Year - 5, today.Year)
            .Where(d => d < today && d.AddDays(-21) >= first).ToList();
        if (occurrences.Count == 0) return null;

        var baseline = totalOrders / spanWindows;
        if (baseline <= 0) return null;

        var average = occurrences.Average(d => days.Where(x => x.Date >= d.AddDays(-21) && x.Date < d).Sum(x => x.Orders));
        return Math.Round(average / baseline, 2);
    }

    /// <summary>The occasions to prepare for by default: the three with the highest lift, or a sensible trio when there is no history.</summary>
    public static List<string> DefaultChoice(IReadOnlyList<(DateOnly Date, int Orders)> days, DateOnly today)
    {
        var ranked = All
            .Select(o => (o.Code, Lift: Lift(o.Code, days, today)))
            .Where(x => x.Lift is >= 1.2)
            .OrderByDescending(x => x.Lift)
            .Select(x => x.Code)
            .Take(3).ToList();

        foreach (var fallback in new[] { "hanukkah", "valentine", "tu_bav" })
            if (ranked.Count < 3 && !ranked.Contains(fallback)) ranked.Add(fallback);

        return ranked;
    }
}

public enum OccasionStep
{
    None,
    Ask,
    Remind,
    Checklist,
    Results,
}

/// <summary>When, in the weeks around an occasion, the agent may say something. At most the few touches below.</summary>
public static class OccasionTimeline
{
    public static OccasionStep Decide(int daysUntil, int stage, string state, int mentionCount, DateTime? lastMentionedUtc, DateTime nowUtc)
    {
        if (state is CoachState.Declined or CoachState.Expired) return OccasionStep.None;

        // Never asked: ask once while there is still time to prepare.
        if (stage == 0 && state == CoachState.Suggested && mentionCount == 0)
            return daysUntil is > 7 and <= 56 ? OccasionStep.Ask : OccasionStep.None;

        // Asked, no answer: one gentle reminder a week later.
        if (stage == 1 && state == CoachState.Suggested && mentionCount == 1 && daysUntil > 7)
            return lastMentionedUtc is { } m && nowUtc - m >= TimeSpan.FromDays(7) ? OccasionStep.Remind : OccasionStep.None;

        // She wants help: offer the preparation checklist about three weeks before.
        if (stage == 2 && state == CoachState.Accepted)
            return daysUntil is > 5 and <= 21 ? OccasionStep.Checklist : OccasionStep.None;

        // After the day: one line about how it went, a few days later.
        if (stage == 3 && state == CoachState.Accepted)
            return daysUntil is <= -3 and >= -10 ? OccasionStep.Results : OccasionStep.None;

        return OccasionStep.None;
    }
}
