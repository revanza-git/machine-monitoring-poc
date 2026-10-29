using System.Net;
using System.Text.Json;
using FleetSim;
using Shared;

namespace FleetSim.Tests;

/// <summary>
/// One upload step against a stub server, outcome by outcome, checked
/// against the rules in Uploader.cs. No network, no delays: WaitFor returns
/// the delay it would have slept.
/// </summary>
public class UploadStepTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static void Queue(VirtualAgent agent, int rows)
    {
        for (var i = 0; i < rows; i++) agent.Enqueue(SampleKinds.CpuMem, DateTimeOffset.UtcNow, new { i });
    }

    private static JsonElement Body(StubServer server, int call = 0) => JsonDocument.Parse(server.Calls[call].Body).RootElement;

    private static long[] Seqs(JsonElement body) => body.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("seq").GetInt64()).ToArray();

    [Fact]
    public async Task Ack_deletes_the_rows_resets_the_backoff_and_adopts_the_config_version()
    {
        var (agent, server, stats) = StubServer.Agent(_ => StubServer.Ack(3, new AgentConfig(4, 60, 300, null, [])));
        Queue(agent, 3);
        agent.WaitFor(new VirtualAgent.Outcome.Failed("earlier"), Interval);   // one failure on the books
        Assert.Equal(1, agent.BackoffFailures);

        var outcome = await agent.SendOnceAsync(stopping: false, CancellationToken.None);

        Assert.IsType<VirtualAgent.Outcome.Sent>(outcome);
        Assert.Equal(0, agent.Depth);
        Assert.Equal(0, agent.BackoffFailures);
        Assert.Equal(4, agent.ConfigVersion);
        Assert.Null(agent.LastError);
        Assert.Equal(Interval, agent.WaitFor(outcome, Interval));

        var body = Body(server);
        Assert.Equal("sim-key-0001", server.Calls[0].ApiKey);
        Assert.Equal("SIM-0001", body.GetProperty("machine_id").GetString());
        Assert.Equal(VirtualAgent.AgentVersion, body.GetProperty("agent_version").GetString());
        Assert.Equal([1L, 2L, 3L], Seqs(body));
        Assert.Equal(0, body.GetProperty("heartbeat").GetProperty("config_version").GetInt32());   // what it ran before this ack
        Assert.False(body.GetProperty("heartbeat").GetProperty("stopping").GetBoolean());

        var summary = stats.Summarize("test", 1, "stub", 7, null, [agent], storm: false);
        Assert.Equal(1, summary.Outcomes[Stats.Ok]);
        Assert.Equal(3, summary.RowsSent);
        Assert.Equal(3, summary.RowsAcked);
    }

    [Fact]
    public async Task Next_batch_reports_the_adopted_version_so_the_server_stops_sending_config()
    {
        var (agent, server, _) = StubServer.Agent(_ => StubServer.Ack(0, new AgentConfig(2, 60, 300, null, [])));
        await agent.SendOnceAsync(false, CancellationToken.None);
        await agent.SendOnceAsync(false, CancellationToken.None);
        Assert.Equal(2, Body(server, 1).GetProperty("heartbeat").GetProperty("config_version").GetInt32());
    }

    [Fact]
    public async Task Full_batch_is_followed_at_once_and_leaves_the_rest_queued()
    {
        var (agent, server, _) = StubServer.Agent(_ => StubServer.Ack(2), maxRows: 2);
        Queue(agent, 3);

        var outcome = await agent.SendOnceAsync(false, CancellationToken.None);

        Assert.IsType<VirtualAgent.Outcome.SentFull>(outcome);
        Assert.Equal(TimeSpan.Zero, agent.WaitFor(outcome, Interval));
        Assert.Equal(1, agent.Depth);
        Assert.Equal([1L, 2L], Seqs(Body(server)));
    }

    [Fact]
    public async Task Throttled_keeps_the_rows_and_waits_retry_after_plus_jitter()
    {
        var (agent, _, stats) = StubServer.Agent(_ => StubServer.TooManyRequests(5));
        Queue(agent, 2);

        var outcome = await agent.SendOnceAsync(false, CancellationToken.None);

        var throttled = Assert.IsType<VirtualAgent.Outcome.Throttled>(outcome);
        Assert.Equal(TimeSpan.FromSeconds(5), throttled.RetryAfter);
        Assert.Equal(2, agent.Depth);
        Assert.Equal(1, agent.Throttled);
        Assert.StartsWith("429", agent.LastError);
        Assert.Equal(0, agent.BackoffFailures);   // Retry-After replaces the backoff, it does not add to it
        for (var i = 0; i < 100; i++)
            Assert.InRange(agent.WaitFor(outcome, Interval), TimeSpan.FromSeconds(6.25), TimeSpan.FromSeconds(7.5));
        Assert.Equal(1, stats.Summarize("test", 1, "stub", 7, null, [agent], false).Outcomes[Stats.Throttled]);
    }

    [Fact]
    public async Task Server_error_keeps_the_rows_and_waits_the_backoff()
    {
        var (agent, _, stats) = StubServer.Agent(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Queue(agent, 2);

        var outcome = await agent.SendOnceAsync(false, CancellationToken.None);

        Assert.Equal("HTTP 500", Assert.IsType<VirtualAgent.Outcome.Failed>(outcome).Reason);
        Assert.Equal("HTTP 500", agent.LastError);
        Assert.Equal(2, agent.Depth);
        Assert.InRange(agent.WaitFor(outcome, Interval), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5));   // first backoff: half to all of 5 s
        Assert.Equal(1, agent.BackoffFailures);
        Assert.Equal(1, stats.Summarize("test", 1, "stub", 7, null, [agent], false).Outcomes[Stats.ServerError]);
    }

    [Fact]
    public async Task Unregistered_key_stops_the_run_instead_of_backing_off()
    {
        var (agent, _, stats) = StubServer.Agent(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Queue(agent, 2);

        var ex = await Assert.ThrowsAsync<NotRegisteredException>(() => agent.SendOnceAsync(false, CancellationToken.None));

        Assert.Contains("SIM-0001", ex.Message);
        Assert.Contains("prepare", ex.Message);
        Assert.Equal(2, agent.Depth);
        Assert.Equal(0, agent.BackoffFailures);   // no backoff: a retry could not help
        Assert.Equal(1, stats.Summarize("test", 1, "stub", 7, null, [agent], false).Outcomes[Stats.Unauthorized]);
    }

    [Fact]
    public async Task Other_client_errors_and_exceptions_are_counted_by_kind()
    {
        var (agent400, _, stats400) = StubServer.Agent(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        Assert.IsType<VirtualAgent.Outcome.Failed>(await agent400.SendOnceAsync(false, CancellationToken.None));
        Assert.Equal(1, stats400.Summarize("test", 1, "stub", 7, null, [agent400], false).Outcomes[Stats.ClientError]);

        var (agentEx, _, statsEx) = StubServer.Agent(_ => throw new HttpRequestException("connection refused"));
        var outcome = await agentEx.SendOnceAsync(false, CancellationToken.None);
        Assert.Equal("connection refused", Assert.IsType<VirtualAgent.Outcome.Failed>(outcome).Reason);
        Assert.Equal(1, statsEx.Summarize("test", 1, "stub", 7, null, [agentEx], false).Outcomes["exception:HttpRequestException"]);
    }

    [Fact]
    public async Task Lost_ack_keeps_the_rows_so_the_next_batch_resends_the_same_seqs()
    {
        var (agent, server, stats) = StubServer.Agent(_ => StubServer.Ack(3), lostAckPct: 100);
        Queue(agent, 3);

        var first = await agent.SendOnceAsync(false, CancellationToken.None);
        var second = await agent.SendOnceAsync(false, CancellationToken.None);

        Assert.IsType<VirtualAgent.Outcome.Failed>(first);
        Assert.IsType<VirtualAgent.Outcome.Failed>(second);
        Assert.Equal(3, agent.Depth);
        Assert.Equal(Seqs(Body(server, 0)), Seqs(Body(server, 1)));
        Assert.Equal("ack lost (simulated)", agent.LastError);

        var summary = stats.Summarize("test", 1, "stub", 7, null, [agent], false);
        Assert.Equal(2, summary.LostAcks);
        Assert.Equal(6, summary.RowsSent);
        Assert.Equal(0, summary.RowsAcked);
    }

    [Fact]
    public async Task Stopping_heartbeat_carries_no_rows_and_the_flag()
    {
        var (agent, server, _) = StubServer.Agent(_ => StubServer.Ack(0));
        Queue(agent, 2);

        var outcome = await agent.SendOnceAsync(stopping: true, CancellationToken.None);

        Assert.IsType<VirtualAgent.Outcome.Sent>(outcome);
        var body = Body(server);
        Assert.True(body.GetProperty("heartbeat").GetProperty("stopping").GetBoolean());
        Assert.Empty(Seqs(body));
        Assert.Equal(2, body.GetProperty("heartbeat").GetProperty("outbox_depth").GetInt64());
        Assert.Equal(2, agent.Depth);   // an ack of 0 deletes nothing
    }

    [Fact]
    public async Task Stopping_heartbeat_is_never_a_lost_ack()
    {
        var (agent, _, stats) = StubServer.Agent(_ => StubServer.Ack(0), lostAckPct: 100);
        Assert.IsType<VirtualAgent.Outcome.Sent>(await agent.SendOnceAsync(stopping: true, CancellationToken.None));
        Assert.Equal(0, stats.Summarize("test", 1, "stub", 7, null, [agent], false).LostAcks);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var (agent, _, _) = StubServer.Agent(_ => StubServer.Ack(0));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.SendOnceAsync(false, cts.Token));
    }
}
