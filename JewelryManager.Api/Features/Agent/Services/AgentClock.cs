namespace JewelryManager.Api.Features.Agent.Services;

/// <summary>Israel's calendar for "today" and "this month", so budgets and summaries roll over at local midnight.</summary>
public static class AgentClock
{
    private static readonly TimeZoneInfo Israel = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Asia/Jerusalem", "Israel Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary>The current time. Tests replace it to try a Friday morning or a quiet night.</summary>
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public static DateTime LocalNow() => TimeZoneInfo.ConvertTimeFromUtc(UtcNow(), Israel);

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Israel);

    public static DateOnly LocalToday() => DateOnly.FromDateTime(LocalNow());

    public static DateTime StartOfTodayUtc() => ToUtc(LocalNow().Date);

    public static DateTime StartOfMonthUtc()
    {
        var now = LocalNow();
        return ToUtc(new DateTime(now.Year, now.Month, 1));
    }

    private static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Israel);
}
