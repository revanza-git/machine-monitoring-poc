namespace Server.Api;

/// <summary>
/// The design's rule for chart resolution: raw points for up to an hour,
/// 5-minute buckets for up to a week, hourly beyond that. A week of raw rows
/// at one per minute is about ten thousand per machine, which buckets
/// cheaply on the fly; past a week the hourly continuous aggregate answers.
/// </summary>
public static class MetricsResolution
{
    public const string Raw = "raw";
    public const string FiveMinute = "5m";
    public const string Hourly = "1h";

    public static (string Name, TimeSpan? Bucket) Pick(TimeSpan range) => range switch
    {
        _ when range <= TimeSpan.FromHours(1) => (Raw, null),
        _ when range <= TimeSpan.FromDays(7) => (FiveMinute, TimeSpan.FromMinutes(5)),
        _ => (Hourly, TimeSpan.FromHours(1)),
    };
}
