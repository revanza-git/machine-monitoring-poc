using Server.Api;

namespace Server.Tests;

/// <summary>The design's thresholds: raw to an hour, 5-minute to a week, hourly beyond.</summary>
public class MetricsResolutionTests
{
    [Theory]
    [InlineData(5, MetricsResolution.Raw)]
    [InlineData(60, MetricsResolution.Raw)]
    [InlineData(61, MetricsResolution.FiveMinute)]
    [InlineData(6 * 60, MetricsResolution.FiveMinute)]
    [InlineData(24 * 60, MetricsResolution.FiveMinute)]
    [InlineData(7 * 24 * 60, MetricsResolution.FiveMinute)]
    [InlineData(7 * 24 * 60 + 1, MetricsResolution.Hourly)]
    [InlineData(90 * 24 * 60, MetricsResolution.Hourly)]
    public void Range_in_minutes_picks_the_designed_resolution(int minutes, string expected)
    {
        Assert.Equal(expected, MetricsResolution.Pick(TimeSpan.FromMinutes(minutes)).Name);
    }

    [Fact]
    public void Raw_has_no_bucket_and_the_others_match_their_name()
    {
        Assert.Null(MetricsResolution.Pick(TimeSpan.FromMinutes(30)).Bucket);
        Assert.Equal(TimeSpan.FromMinutes(5), MetricsResolution.Pick(TimeSpan.FromDays(1)).Bucket);
        Assert.Equal(TimeSpan.FromHours(1), MetricsResolution.Pick(TimeSpan.FromDays(30)).Bucket);
    }
}
