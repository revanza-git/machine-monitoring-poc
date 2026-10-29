using System.Net;
using FleetSim;
using Shared;

namespace FleetSim.Tests;

/// <summary>The storm backlog is the design's outage made literal: one cpu_mem per minute, one drive sample per five, ending now.</summary>
public class BacklogTests
{
    [Theory]
    [InlineData(1, 72)]
    [InlineData(6, 432)]
    [InlineData(24, 1728)]
    public void Row_count_matches_the_design_arithmetic(int hours, int rows)
    {
        var agent = StubServer.Agent(_ => new HttpResponseMessage(HttpStatusCode.OK)).Agent;
        agent.FillBacklog(TimeSpan.FromHours(hours), DateTimeOffset.UtcNow);
        Assert.Equal(rows, agent.Depth);
    }

    [Fact]
    public void Timestamps_are_historical_and_end_now_with_increasing_seqs()
    {
        var agent = StubServer.Agent(_ => new HttpResponseMessage(HttpStatusCode.OK)).Agent;
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        agent.FillBacklog(TimeSpan.FromHours(6), now);
        var rows = agent.PeekBatch(10_000);

        Assert.Equal(now.AddMinutes(-359), rows[0].SampledAt);
        Assert.Equal(now, rows[^1].SampledAt);
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.Second.Seq == p.First.Seq + 1), "seq must increase by one");
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.Second.SampledAt >= p.First.SampledAt), "time must not go backwards");
        Assert.Equal(360, rows.Count(r => r.Kind == SampleKinds.CpuMem));
        Assert.Equal(72, rows.Count(r => r.Kind == SampleKinds.Drive));
        Assert.Equal(SampleKinds.Drive, rows[^1].Kind);   // the last minute is a multiple of five
    }
}
