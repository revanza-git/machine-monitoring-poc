using System.Net;
using FleetSim;
using Shared;

namespace FleetSim.Tests;

/// <summary>The in-memory outbox must behave like Outbox.cs: peek leaves rows in place, delete-through removes only acked seqs.</summary>
public class OutboxTests
{
    private static VirtualAgent Agent() => StubServer.Agent(_ => new HttpResponseMessage(HttpStatusCode.OK)).Agent;

    [Fact]
    public void Seq_starts_at_one_and_only_increases()
    {
        var agent = Agent();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, agent.Enqueue(SampleKinds.CpuMem, now, new { }));
        Assert.Equal(2, agent.Enqueue(SampleKinds.CpuMem, now, new { }));
        agent.DeleteThrough(2);
        Assert.Equal(3, agent.Enqueue(SampleKinds.CpuMem, now, new { }));   // never reused after a delete
    }

    [Fact]
    public void Peek_returns_the_oldest_rows_in_order_without_removing_them()
    {
        var agent = Agent();
        for (var i = 0; i < 5; i++) agent.Enqueue(SampleKinds.CpuMem, DateTimeOffset.UtcNow, new { i });

        var batch = agent.PeekBatch(3);

        Assert.Equal([1L, 2L, 3L], batch.Select(s => s.Seq));
        Assert.Equal(5, agent.Depth);
    }

    [Fact]
    public void Delete_through_removes_rows_at_or_below_the_ack_only()
    {
        var agent = Agent();
        for (var i = 0; i < 5; i++) agent.Enqueue(SampleKinds.CpuMem, DateTimeOffset.UtcNow, new { i });

        Assert.Equal(3, agent.DeleteThrough(3));
        Assert.Equal(2, agent.Depth);
        Assert.Equal([4L, 5L], agent.PeekBatch(10).Select(s => s.Seq));
        Assert.Equal(0, agent.DeleteThrough(3));   // idempotent
    }

    [Fact]
    public void Steady_cycle_queues_cpu_mem_every_time_and_drives_every_fifth()
    {
        var agent = Agent();
        for (var i = 0; i < 10; i++) agent.QueueSteady(DateTimeOffset.UtcNow);

        var kinds = agent.PeekBatch(100).Select(s => s.Kind).ToList();
        Assert.Equal(10, kinds.Count(k => k == SampleKinds.CpuMem));
        Assert.Equal(2, kinds.Count(k => k == SampleKinds.Drive));
        Assert.Equal(SampleKinds.Drive, kinds[1]);   // the first cycle carries a drive sample so the portal shows drives at once
    }
}
