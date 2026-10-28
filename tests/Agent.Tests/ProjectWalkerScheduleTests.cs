using Agent;

namespace Agent.Tests;

/// <summary>When the next walk is due: nightly at the configured local hour, or just the interval when it is shorter than a day.</summary>
public class ProjectWalkerScheduleTests
{
    // A fixed zone so the result never depends on the machine running the tests. UTC+7, no DST.
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+7", TimeSpan.FromHours(7), "Test+7", "Test+7");

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Short_interval_just_repeats()
    {
        var now = Local(2026, 10, 28, 14, 0);
        Assert.Equal(now.AddSeconds(120), ProjectWalker.NextDue(now, 120, walkHour: 2, Zone));
    }

    [Fact]
    public void Daily_interval_waits_for_tonight()
    {
        var now = Local(2026, 10, 28, 14, 0);
        Assert.Equal(Local(2026, 10, 29, 2, 0), ProjectWalker.NextDue(now, 86400, walkHour: 2, Zone));
    }

    [Fact]
    public void Daily_interval_before_the_hour_runs_later_today()
    {
        var now = Local(2026, 10, 28, 1, 30);
        Assert.Equal(Local(2026, 10, 28, 2, 0), ProjectWalker.NextDue(now, 86400, walkHour: 2, Zone));
    }

    [Fact]
    public void Exactly_at_the_hour_means_tomorrow()
    {
        var now = Local(2026, 10, 28, 2, 0);
        Assert.Equal(Local(2026, 10, 29, 2, 0), ProjectWalker.NextDue(now, 86400, walkHour: 2, Zone));
    }

    [Fact]
    public void Hour_is_read_in_the_given_zone_not_utc()
    {
        var now = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);   // 03:00 next day in UTC+7
        Assert.Equal(Local(2026, 10, 30, 2, 0), ProjectWalker.NextDue(now, 86400, walkHour: 2, Zone));
    }
}
