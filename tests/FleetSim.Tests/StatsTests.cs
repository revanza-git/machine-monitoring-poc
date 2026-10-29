using FleetSim;

namespace FleetSim.Tests;

public class StatsTests
{
    [Fact]
    public void Percentiles_are_nearest_rank_over_the_sorted_list()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(50, Stats.Percentile(sorted, 0.50));
        Assert.Equal(95, Stats.Percentile(sorted, 0.95));
        Assert.Equal(99, Stats.Percentile(sorted, 0.99));
        Assert.Equal(100, Stats.Percentile(sorted, 1.0));
        Assert.Equal(1, Stats.Percentile(sorted, 0.0));
    }

    [Fact]
    public void Empty_list_gives_zero_not_an_exception()
    {
        Assert.Equal(0, Stats.Percentile([], 0.99));
        var summary = new Stats().Summarize("test", 0, "stub", 1, null, [], storm: true);
        Assert.Equal(0, summary.Latency.Count);
        Assert.Null(summary.DrainS);
        Assert.Null(summary.ThrottledPerAgent);
    }

    [Fact]
    public void Outcomes_are_counted_by_kind_and_rows_sent_are_summed()
    {
        var stats = new Stats();
        stats.Record(Stats.Ok, TimeSpan.FromMilliseconds(10), rowsSent: 5);
        stats.Record(Stats.Ok, TimeSpan.FromMilliseconds(30), rowsSent: 7);
        stats.Record(Stats.Throttled, TimeSpan.FromMilliseconds(1));
        stats.Record(Stats.Exception(new TimeoutException()), TimeSpan.FromSeconds(30));
        stats.Acked(12);

        var summary = stats.Summarize("test", 0, "stub", 1, targetPerS: 2.5, [], storm: false);

        Assert.Equal(4, summary.Batches);
        Assert.Equal(2, summary.Outcomes[Stats.Ok]);
        Assert.Equal(1, summary.Outcomes[Stats.Throttled]);
        Assert.Equal(1, summary.Outcomes["exception:TimeoutException"]);
        Assert.Equal(12, summary.RowsSent);
        Assert.Equal(12, summary.RowsAcked);
        Assert.Equal(30000, summary.Latency.MaxMs);
        Assert.Equal(2.5, summary.TargetPerS);
    }

    [Fact]
    public void In_flight_high_water_mark_survives_exits()
    {
        var stats = new Stats();
        stats.Enter(); stats.Enter(); stats.Enter();
        stats.Exit();
        stats.Enter();
        Assert.Equal(3, stats.InFlight);
        Assert.Equal(3, stats.Summarize("test", 0, "stub", 1, null, [], false).MaxInFlight);
    }

    [Fact]
    public void Summary_round_trips_as_snake_case_json()
    {
        var stats = new Stats();
        stats.Record(Stats.Ok, TimeSpan.FromMilliseconds(12), rowsSent: 1);
        var path = Path.Combine(Path.GetTempPath(), $"fleetsim-{Guid.NewGuid():N}", "steady-1.json");

        stats.Summarize("steady", 1, "stub", 1, 1.0, [], false).WriteJson(path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"rows_sent\": 1", json);
        Assert.Contains("\"p99_ms\": 12", json);
        Assert.DoesNotContain("drain_s", json);   // nulls are left out
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
