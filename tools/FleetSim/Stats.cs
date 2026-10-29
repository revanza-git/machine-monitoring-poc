using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetSim;

/// <summary>
/// Fleet-wide counters shared by every virtual agent: outcomes, every
/// latency (tens of thousands at most, so percentiles are computed exactly
/// at the end), rows sent and acknowledged, and the in-flight high-water
/// mark. One lock; the agents post a few times a second at most.
/// </summary>
public sealed class Stats
{
    public const string Ok = "2xx";
    public const string Throttled = "429";
    public const string Unauthorized = "401";
    public const string ServerError = "5xx";
    public const string ClientError = "4xx-other";
    public static string Exception(Exception ex) => "exception:" + ex.GetType().Name;

    private readonly object _gate = new();
    private readonly List<double> _latenciesMs = [];
    private readonly SortedDictionary<string, long> _outcomes = new(StringComparer.Ordinal);
    private long _batches, _rowsSent, _rowsAcked, _lostAcks;
    private int _inFlight, _maxInFlight;
    private DateTimeOffset? _firstPost, _lastAck;

    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Around each POST: the in-flight count and its maximum are the storm's concurrency figure.</summary>
    public void Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        lock (_gate) _firstPost ??= DateTimeOffset.UtcNow;
    }

    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>One request finished with <paramref name="outcome"/> after <paramref name="latency"/>.</summary>
    public void Record(string outcome, TimeSpan latency, int rowsSent = 0)
    {
        lock (_gate)
        {
            _batches++;
            _rowsSent += rowsSent;
            _latenciesMs.Add(latency.TotalMilliseconds);
            _outcomes[outcome] = _outcomes.GetValueOrDefault(outcome) + 1;
            if (outcome == Ok) _lastAck = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Rows deleted from an outbox because an ack covered them.</summary>
    public void Acked(int rows)
    {
        lock (_gate) _rowsAcked += rows;
    }

    public void LostAck()
    {
        lock (_gate) _lostAcks++;
    }

    /// <summary>The line printed every 30 seconds while a run is going.</summary>
    public string ProgressLine(long totalDepth, double? targetPerS)
    {
        lock (_gate)
        {
            var s = Elapsed.Elapsed.TotalSeconds;
            var rate = s > 0 ? _batches / s : 0;
            var target = targetPerS is { } t ? $", target {t:0.0}/s" : "";
            var outcomes = _outcomes.Count == 0 ? "-" : string.Join(" ", _outcomes.Select(kv => $"{kv.Key}={kv.Value}"));
            return $"[{s,6:0}s] batches {_batches} ({rate:0.0}/s{target}) | {outcomes} | p99 {Percentile(Sorted(), 0.99):0} ms | depth {totalDepth} | in-flight {InFlight}";
        }
    }

    public RunSummary Summarize(string command, int agents, string server, int seed, double? targetPerS,
        IReadOnlyList<VirtualAgent> fleet, bool storm)
    {
        lock (_gate)
        {
            var sorted = Sorted();
            var s = Elapsed.Elapsed.TotalSeconds;
            var throttled = fleet.Select(a => (double)a.Throttled).OrderBy(x => x).ToList();
            return new RunSummary(
                Command: command,
                Agents: agents,
                Server: server,
                Seed: seed,
                StartedAt: StartedAt,
                ElapsedS: Math.Round(s, 1),
                Batches: _batches,
                AchievedPerS: Math.Round(s > 0 ? _batches / s : 0, 2),
                TargetPerS: targetPerS is { } t ? Math.Round(t, 2) : null,
                Outcomes: new Dictionary<string, long>(_outcomes),
                Latency: new LatencySummary(
                    P50Ms: Math.Round(Percentile(sorted, 0.50), 1),
                    P95Ms: Math.Round(Percentile(sorted, 0.95), 1),
                    P99Ms: Math.Round(Percentile(sorted, 0.99), 1),
                    MaxMs: Math.Round(sorted.Count == 0 ? 0 : sorted[^1], 1),
                    Count: sorted.Count),
                RowsSent: _rowsSent,
                RowsAcked: _rowsAcked,
                LostAcks: _lostAcks,
                MaxInFlight: _maxInFlight,
                TotalDepth: fleet.Sum(a => (long)a.Depth),
                AgentsNotDrained: fleet.Where(a => a.Depth > 0).Select(a => $"{a.Name}:{a.Depth}").ToList(),
                DrainS: storm && _firstPost is { } first && _lastAck is { } last ? Math.Round((last - first).TotalSeconds, 1) : null,
                ThrottledPerAgent: storm && throttled.Count > 0
                    ? new ThrottleSummary((int)throttled[0], Percentile(throttled, 0.5), (int)throttled[^1], throttled.Count(x => x > 0))
                    : null);
        }
    }

    /// <summary>Nearest-rank percentile over an ascending list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private List<double> Sorted()
    {
        var copy = new List<double>(_latenciesMs);
        copy.Sort();
        return copy;
    }
}

public sealed record LatencySummary(double P50Ms, double P95Ms, double P99Ms, double MaxMs, long Count);

public sealed record ThrottleSummary(int Min, double P50, int Max, int AgentsThrottled);

/// <summary>The final summary: printed as text and written as JSON to --out.</summary>
public sealed record RunSummary(
    string Command,
    int Agents,
    string Server,
    int Seed,
    DateTimeOffset StartedAt,
    double ElapsedS,
    long Batches,
    double AchievedPerS,
    double? TargetPerS,
    Dictionary<string, long> Outcomes,
    LatencySummary Latency,
    long RowsSent,
    long RowsAcked,
    long LostAcks,
    int MaxInFlight,
    long TotalDepth,
    IReadOnlyList<string> AgentsNotDrained,
    double? DrainS,
    ThrottleSummary? ThrottledPerAgent)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToText()
    {
        var lines = new List<string>
        {
            $"=== {Command}: {Agents} agents, {ElapsedS:0.#}s ===",
            $"batches     {Batches} ({AchievedPerS:0.00}/s" + (TargetPerS is { } t ? $", target {t:0.00}/s)" : ")"),
            $"outcomes    {(Outcomes.Count == 0 ? "-" : string.Join("  ", Outcomes.Select(kv => $"{kv.Key}={kv.Value}")))}",
            $"latency ms  p50 {Latency.P50Ms:0.#}  p95 {Latency.P95Ms:0.#}  p99 {Latency.P99Ms:0.#}  max {Latency.MaxMs:0.#}  ({Latency.Count} requests)",
            $"rows        sent {RowsSent}, acked {RowsAcked}" + (LostAcks > 0 ? $", acks lost {LostAcks}" : ""),
            $"outbox      depth {TotalDepth}; not drained: {(AgentsNotDrained.Count == 0 ? "none" : string.Join(", ", AgentsNotDrained.Take(10)) + (AgentsNotDrained.Count > 10 ? $" (+{AgentsNotDrained.Count - 10})" : ""))}",
            $"in-flight   max {MaxInFlight}",
        };
        if (DrainS is { } d) lines.Add($"drain       {d:0.#}s from first post to last ack");
        if (ThrottledPerAgent is { } th) lines.Add($"429/agent   min {th.Min}  p50 {th.P50:0.#}  max {th.Max}  ({th.AgentsThrottled} of {Agents} agents throttled)");
        return string.Join(Environment.NewLine, lines);
    }

    public void WriteJson(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
