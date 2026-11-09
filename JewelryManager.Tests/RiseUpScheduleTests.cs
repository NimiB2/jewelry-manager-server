using JewelryManager.Api.Features.RiseUp;

namespace JewelryManager.Tests;

public class RiseUpScheduleTests
{
    [Fact]
    public void LocalNow_ConvertsUtcToIsraelTime()
    {
        // March 10, 2032 is before Israel's summer time, so Israel is two hours ahead of UTC.
        var local = RiseUpSchedule.LocalNow(new DateTimeOffset(2032, 3, 10, 3, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTime(2032, 3, 10, 5, 30, 0), local);
    }

    [Fact]
    public void IsDue_WaitsForSixInTheMorning()
    {
        Assert.False(RiseUpSchedule.IsDue(new DateTime(2032, 3, 10, 5, 59, 0), null));
        Assert.True(RiseUpSchedule.IsDue(new DateTime(2032, 3, 10, 6, 0, 0), null));
    }

    [Fact]
    public void IsDue_RunsOnceADay()
    {
        var sameDay = new DateOnly(2032, 3, 10);

        Assert.False(RiseUpSchedule.IsDue(new DateTime(2032, 3, 10, 14, 0, 0), sameDay));
        Assert.True(RiseUpSchedule.IsDue(new DateTime(2032, 3, 11, 9, 0, 0), sameDay));
    }

    [Fact]
    public void MonthsToSync_IncludesThePreviousMonth_AcrossTheYearEnd()
    {
        var months = RiseUpSchedule.MonthsToSync(new DateTime(2032, 1, 10, 6, 0, 0));

        Assert.Equal(["2031-12", "2032-01"], months);
    }
}
