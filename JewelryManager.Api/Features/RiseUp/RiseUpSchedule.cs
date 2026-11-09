using System.Globalization;

namespace JewelryManager.Api.Features.RiseUp;

/// <summary>When the daily sync runs: once a day, after 06:00 in Israel, judged on Israel's calendar.</summary>
public static class RiseUpSchedule
{
    public const int RunAtHour = 6;

    private static readonly TimeZoneInfo Israel = Find();

    public static DateTime LocalNow(DateTimeOffset utcNow) => TimeZoneInfo.ConvertTime(utcNow, Israel).DateTime;

    public static bool IsDue(DateTime localNow, DateOnly? lastRunDay) =>
        localNow.Hour >= RunAtHour && DateOnly.FromDateTime(localNow) != lastRunDay;

    // The current month and the one before it: late card charges and bank entries land in the previous month.
    public static IReadOnlyList<string> MonthsToSync(DateTime localNow)
    {
        var current = new DateOnly(localNow.Year, localNow.Month, 1);
        return [current.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture), current.ToString("yyyy-MM", CultureInfo.InvariantCulture)];
    }

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Jerusalem", "Israel Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        throw new InvalidOperationException("Israel time zone is not available on this server");
    }
}
