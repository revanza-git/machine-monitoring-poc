using System.Net.Security;
using Agent;

namespace FleetSim;

/// <summary>
/// Runs N virtual agents against one server and collects their statistics.
/// Steady is the design's normal minute at fleet scale; storm is step 3 of
/// its "server down for six hours": every agent holds a backlog and the
/// server has just come back. One HttpClient serves the whole fleet.
/// </summary>
public static class Fleet
{
    private static readonly TimeSpan BackoffInitial = TimeSpan.FromSeconds(5);   // the agent's BackoffInitialS
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(30);

    public static async Task<RunSummary> SteadyAsync(SteadyOptions o, CancellationToken ct)
    {
        using var http = CreateClient(o.Server, o.Agents);
        var stats = new Stats();
        var agents = Create(o.Agents, http, stats, o.BackoffMax, o.Seed, o.LostAckPct);
        var target = o.Agents / o.Interval.TotalSeconds;
        Console.WriteLine($"steady: {o.Agents} agents every {o.Interval.TotalSeconds:0.#}s for {o.Minutes} min against {o.Server} (target {target:0.0} batches/s)");

        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        run.CancelAfter(TimeSpan.FromMinutes(o.Minutes));

        // Each agent's first post is offset by a random fraction of the
        // interval so the fleet never ticks in lockstep, as real agents
        // installed at different times would not.
        var offsets = new Random(o.Seed);
        var tasks = agents.Select(a => RunSteadyAgentAsync(a, o.Interval, offsets.NextDouble(), run.Token)).ToList();
        await RunWithProgressAsync(tasks, stats, agents, target);

        var summary = stats.Summarize("steady", o.Agents, o.Server, o.Seed, target, agents, storm: false);
        await SayGoodbyeAsync(agents);
        return summary;
    }

    public static async Task<RunSummary> StormAsync(StormOptions o, CancellationToken ct)
    {
        using var http = CreateClient(o.Server, o.Agents);
        var stats = new Stats();
        var agents = Create(o.Agents, http, stats, o.BackoffMax, o.Seed, o.LostAckPct);

        var now = DateTimeOffset.UtcNow;
        foreach (var a in agents) a.FillBacklog(o.Backlog, now);
        var rows = agents[0].Depth;
        var start = o.Jitter
            ? $"first post jittered over {o.BackoffMax.TotalSeconds / 2:0}-{o.BackoffMax.TotalSeconds:0}s, where a real backoff sits after a long outage"
            : "no jitter: every agent posts at t=0";
        Console.WriteLine($"storm: {o.Agents} agents with a {o.Backlog.TotalHours:0.#}h backlog ({rows} rows each, {rows * (long)o.Agents} total) against {o.Server}; {start}");

        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        run.CancelAfter(o.Timeout);
        var tasks = agents.Select(a => RunStormAgentAsync(a, o, run.Token)).ToList();
        await RunWithProgressAsync(tasks, stats, agents, targetPerS: null);

        var summary = stats.Summarize("storm", o.Agents, o.Server, o.Seed, null, agents, storm: true);
        if (run.IsCancellationRequested && !ct.IsCancellationRequested)
            Console.WriteLine($"timeout after {o.Timeout.TotalMinutes:0.#} min with {summary.AgentsNotDrained.Count} agents not drained");
        await SayGoodbyeAsync(agents);
        return summary;
    }

    private static async Task RunSteadyAgentAsync(VirtualAgent agent, TimeSpan interval, double offsetFraction, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval * offsetFraction, ct);
            while (true)
            {
                agent.QueueSteady(DateTimeOffset.UtcNow);
                var outcome = await agent.SendOnceAsync(stopping: false, ct);
                var wait = agent.WaitFor(outcome, interval);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // the run ended
        }
    }

    private static async Task RunStormAgentAsync(VirtualAgent agent, StormOptions o, CancellationToken ct)
    {
        try
        {
            if (o.Jitter) await Task.Delay(Backoff.Jitter(o.BackoffMax), ct);
            while (agent.Depth > 0)
            {
                var outcome = await agent.SendOnceAsync(stopping: false, ct);
                var wait = agent.WaitFor(outcome, interval: TimeSpan.Zero);   // a partial batch means the backlog is gone
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // the run ended or timed out
        }
    }

    private static async Task RunWithProgressAsync(List<Task> tasks, Stats stats, IReadOnlyList<VirtualAgent> agents, double? targetPerS)
    {
        var all = Task.WhenAll(tasks);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(ProgressEvery));
            if (!all.IsCompleted)
                Console.WriteLine(stats.ProgressLine(agents.Sum(a => (long)a.Depth), targetPerS));
        }
        await all;
    }

    /// <summary>
    /// The goodbye the real uploader sends on a clean shutdown: a heartbeat
    /// flagged stopping, best effort, so the portal shows the fleet as off
    /// rather than unresponsive. Spread over a few milliseconds per agent so
    /// a thousand goodbyes do not hit the throttle as one burst.
    /// </summary>
    private static async Task SayGoodbyeAsync(IReadOnlyList<VirtualAgent> agents)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(10 * agents.Count));
        var results = await Task.WhenAll(agents.Select(async (a, i) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * i), cts.Token);
                return await a.SendOnceAsync(stopping: true, cts.Token) is VirtualAgent.Outcome.Sent;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }));
        Console.WriteLine($"stopping heartbeats: {results.Count(ok => ok)} of {agents.Count} acknowledged (the portal shows those as off)");
    }

    private static List<VirtualAgent> Create(int n, HttpClient http, Stats stats, TimeSpan backoffMax, int seed, double lostAckPct) =>
        Enumerable.Range(1, n)
            .Select(i => new VirtualAgent(i, http, stats, new Backoff(BackoffInitial, backoffMax), seed, lostAckPct))
            .ToList();

    private static HttpClient CreateClient(string server, int agents)
    {
        Console.WriteLine("warning: server certificate validation is off (development certificate), as in the agent's Development profile");
        var handler = new SocketsHttpHandler
        {
            // One connection per agent at worst, so the client never queues
            // requests the server should be seeing: in-flight and latency
            // then measure the server, not this process.
            MaxConnectionsPerServer = Math.Max(64, agents),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(server),
            Timeout = TimeSpan.FromSeconds(30),   // the agent's timeout
        };
    }
}
