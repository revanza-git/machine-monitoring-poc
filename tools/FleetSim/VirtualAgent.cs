using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent;
using Shared;

namespace FleetSim;

/// <summary>
/// One simulated machine: the identity, outbox and upload rules of the real
/// agent, with an in-memory list instead of SQLite and generated values
/// instead of counters. Mirrors Uploader.cs step for step: peek the oldest
/// rows, post them with a heartbeat, delete only what the ack covers; a full
/// batch goes again at once, a 429 waits Retry-After plus jitter, anything
/// else waits the backoff. <see cref="SendOnceAsync"/> is one upload and
/// <see cref="WaitFor"/> the pacing decision, so the loop in Fleet.cs is a
/// few lines and both halves can be tested without a network.
/// </summary>
public sealed class VirtualAgent
{
    public const string AgentVersion = "sim-0.1.0";
    public const string ApiKeyHeader = "X-Api-Key";

    private readonly HttpClient _http;
    private readonly Stats _stats;
    private readonly Backoff _backoff;
    private readonly Random _rng;
    private readonly Workload _workload;
    private readonly int _maxRows;
    private readonly double _lostAckPct;

    private readonly List<Sample> _outbox = [];   // oldest first, seq ascending
    private readonly object _gate = new();
    private long _seq;
    private int _cycle;

    public string Name { get; }
    public string ApiKey { get; }
    public Guid InstallId { get; } = Guid.NewGuid();   // new per run: every run is a fresh install, nothing dedups against the last one
    public int ConfigVersion { get; private set; }
    public string? LastError { get; private set; }
    public int Throttled { get; private set; }
    public int BackoffFailures => _backoff.Failures;

    public VirtualAgent(int index, HttpClient http, Stats stats, Backoff backoff,
        int seed = 1, double lostAckPct = 0, int maxRows = SampleBatch.MaxRows)
    {
        Name = $"SIM-{index:D4}";
        ApiKey = $"sim-key-{index:D4}";
        _http = http;
        _stats = stats;
        _backoff = backoff;
        _rng = new Random(seed * 100003 + index);
        _workload = new Workload(seed * 100003 + index);
        _maxRows = maxRows;
        _lostAckPct = lostAckPct;
    }

    // ---- the outbox, same semantics as Outbox.cs ----

    /// <summary>Queue one sample under the next seq. Returns that seq.</summary>
    public long Enqueue(string kind, DateTimeOffset sampledAt, object data)
    {
        lock (_gate)
        {
            var sample = new Sample(++_seq, kind, sampledAt, JsonSerializer.SerializeToElement(data, JsonDefaults.Options));
            _outbox.Add(sample);
            return sample.Seq;
        }
    }

    /// <summary>Oldest rows in seq order, at most <paramref name="max"/>, left in place.</summary>
    public List<Sample> PeekBatch(int max)
    {
        lock (_gate) return _outbox.Take(max).ToList();
    }

    /// <summary>Called only after the server acknowledged <paramref name="ackThrough"/>.</summary>
    public int DeleteThrough(long ackThrough)
    {
        lock (_gate) return _outbox.RemoveAll(s => s.Seq <= ackThrough);
    }

    public int Depth
    {
        get { lock (_gate) return _outbox.Count; }
    }

    // ---- what the sampling loop would have queued ----

    /// <summary>
    /// One steady cycle: a cpu_mem sample every time and a drive sample every
    /// fifth (60 s against 5 min in the design). The first cycle carries both
    /// so the portal shows drives at once.
    /// </summary>
    public void QueueSteady(DateTimeOffset now)
    {
        Enqueue(SampleKinds.CpuMem, now, _workload.NextCpuMem());
        if (_cycle++ % 5 == 0) Enqueue(SampleKinds.Drive, now, _workload.NextDrives());
    }

    /// <summary>
    /// The backlog of an outage: one cpu_mem per minute and one drive sample
    /// per five, at historical timestamps ending at <paramref name="now"/>.
    /// Six hours is 432 rows, a day 1,728.
    /// </summary>
    public void FillBacklog(TimeSpan outage, DateTimeOffset now)
    {
        var minutes = (int)outage.TotalMinutes;
        for (var i = minutes - 1; i >= 0; i--)
        {
            var at = now.AddMinutes(-i);
            Enqueue(SampleKinds.CpuMem, at, _workload.NextCpuMem());
            if (i % 5 == 0) Enqueue(SampleKinds.Drive, at, _workload.NextDrives());
        }
    }

    // ---- one upload, as Uploader.TrySendBatchAsync ----

    public abstract record Outcome
    {
        public sealed record Sent : Outcome;
        public sealed record SentFull : Outcome;
        public sealed record Throttled(TimeSpan RetryAfter) : Outcome;
        public sealed record Failed(string Reason) : Outcome;
    }

    public async Task<Outcome> SendOnceAsync(bool stopping, CancellationToken ct)
    {
        List<Sample> rows = stopping ? [] : PeekBatch(_maxRows);
        var batch = new SampleBatch(
            MachineId: Name,
            InstallId: InstallId,
            AgentVersion: AgentVersion,
            Samples: rows,
            Heartbeat: new Heartbeat(
                ActiveUser: "SIM\\user",
                LastUser: "SIM\\user",
                OutboxDepth: Depth,
                RestartCount: 1,
                RestartsLastHour: 1,        // below the restart-loop threshold of three
                LastError: LastError,
                ConfigVersion: ConfigVersion,
                Stopping: stopping));

        var clock = Stopwatch.StartNew();
        _stats.Enter();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Samples)
            {
                Content = JsonContent.Create(batch, options: JsonDefaults.Options),
            };
            request.Headers.Add(ApiKeyHeader, ApiKey);   // per request, not a default header: one client serves the whole fleet
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? _backoff.Next();
                Throttled++;
                _stats.Record(Stats.Throttled, clock.Elapsed);
                return Fail(new Outcome.Throttled(retryAfter), $"429, Retry-After {retryAfter.TotalSeconds:0.#}s");
            }

            if (!response.IsSuccessStatusCode)
            {
                _stats.Record((int)response.StatusCode >= 500 ? Stats.ServerError : Stats.ClientError, clock.Elapsed);
                return Fail(new Outcome.Failed($"HTTP {(int)response.StatusCode}"));
            }

            var ack = await response.Content.ReadFromJsonAsync<AckResponse>(JsonDefaults.Options, ct)
                      ?? throw new InvalidOperationException("empty ack");
            _stats.Record(Stats.Ok, clock.Elapsed, rowsSent: rows.Count);   // latency includes reading the body

            if (!stopping && _lostAckPct > 0 && _rng.NextDouble() * 100 < _lostAckPct)
            {
                // The server stored the rows and replied, but the reply never
                // reached the agent. The rows stay and are resent after the
                // backoff; the server must drop them as already seen. The
                // goodbye is exempt: it carries no rows, and the run is over.
                _stats.LostAck();
                return Fail(new Outcome.Failed("ack lost (simulated)"));
            }

            var deleted = DeleteThrough(ack.AckThrough);
            _stats.Acked(deleted);
            _backoff.Reset();
            LastError = null;
            if (ack.Config is { } config)
                ConfigVersion = config.Version;   // from now on the server stops attaching config, as with a real fleet

            return rows.Count == _maxRows ? new Outcome.SentFull() : new Outcome.Sent();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Connection refused, timeout, a 200 that is not JSON: the rows
            // stay and the backoff decides when to try again. Only the run
            // ending is allowed out of here.
            _stats.Record(Stats.Exception(ex), clock.Elapsed);
            return Fail(new Outcome.Failed(ex.GetBaseException().Message));
        }
        finally
        {
            _stats.Exit();
        }

        Outcome Fail(Outcome outcome, string? reason = null)
        {
            LastError = reason ?? (outcome as Outcome.Failed)?.Reason;
            return outcome;
        }
    }

    /// <summary>
    /// The uploader's pacing: a full batch goes again at once, a normal one
    /// waits the interval, a 429 waits Retry-After plus half of it jittered,
    /// anything else waits the exponential backoff.
    /// </summary>
    public TimeSpan WaitFor(Outcome outcome, TimeSpan interval) => outcome switch
    {
        Outcome.SentFull => TimeSpan.Zero,
        Outcome.Sent => interval,
        Outcome.Throttled t => t.RetryAfter + Backoff.Jitter(t.RetryAfter / 2),
        _ => _backoff.Next(),
    };
}
