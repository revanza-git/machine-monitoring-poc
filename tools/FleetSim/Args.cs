using System.Globalization;

namespace FleetSim;

/// <summary>
/// Hand-parsed command line: one command word, then <c>--flag value</c>
/// pairs and bare <c>--switch</c>es. Three commands and a dozen flags do not
/// justify a parser library, and this shape is easy to test. Every flag read
/// is remembered so an unknown one can be reported instead of ignored.
/// </summary>
public sealed class Args
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public string Command { get; }

    private Args(string command) => Command = command;

    public static Args Parse(string[] argv)
    {
        if (argv.Length == 0 || argv[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("expected a command: prepare, steady or storm");

        var args = new Args(argv[0]);
        for (var i = 1; i < argv.Length; i++)
        {
            var token = argv[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument '{token}'");
            if (args._flags.ContainsKey(token))
                throw new ArgumentException($"{token} given twice");
            var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
            args._flags[token] = hasValue ? argv[++i] : null;
        }
        return args;
    }

    private string? Value(string name)
    {
        _used.Add(name);
        return _flags.TryGetValue(name, out var v) ? v : null;
    }

    public string Get(string name, string fallback) => Value(name) ?? fallback;

    public bool Has(string name)
    {
        _used.Add(name);
        return _flags.ContainsKey(name);
    }

    public int GetInt(string name, int fallback) => Value(name) is { } v ? ParseInt(name, v) : fallback;

    public int RequireInt(string name) => Value(name) is { } v ? ParseInt(name, v) : throw new ArgumentException($"{name} is required");

    public double GetDouble(string name, double fallback) => Value(name) is { } v
        ? double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"{name}: '{v}' is not a number")
        : fallback;

    /// <summary>Call after every option has been read: a flag nobody asked for is a typo, not a no-op.</summary>
    public void EnsureAllUsed()
    {
        var unknown = _flags.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"unknown flag(s): {string.Join(", ", unknown)}");
    }

    private static int ParseInt(string name, string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"{name}: '{v}' is not a whole number");
}

/// <summary>
/// Where things are when nothing says otherwise. On the host the repository
/// root is found by walking up from the binary; in the container the
/// FLEETSIM_* environment variables point at the mounted folders.
/// </summary>
public sealed class Defaults
{
    public string? RepoRoot { get; } = FindRepoRoot();

    public string Server => Environment.GetEnvironmentVariable("FLEETSIM_SERVER") ?? "https://localhost:7181";

    public string MachinesFile => Environment.GetEnvironmentVariable("FLEETSIM_MACHINES_FILE")
        ?? (RepoRoot is null ? "machines.json" : Path.Combine(RepoRoot, "deploy", "config", "machines.json"));

    public string ServerSettings => Environment.GetEnvironmentVariable("FLEETSIM_SERVER_SETTINGS")
        ?? (RepoRoot is null ? "server-appsettings.json" : Path.Combine(RepoRoot, "src", "Server", "appsettings.json"));

    public string ResultsDir => Environment.GetEnvironmentVariable("FLEETSIM_RESULTS")
        ?? (RepoRoot is null ? "results" : Path.Combine(RepoRoot, "tools", "FleetSim", "results"));

    public string ResultPath(string command, int agents) =>
        Path.Combine(ResultsDir, $"{command}-{agents}-{DateTime.UtcNow:yyyyMMdd-HHmm}.json");

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MachineMonitoring.slnx")))
                return dir.FullName;
        return null;
    }
}

public sealed record PrepareOptions(int Agents, string Out, string ServerSettings)
{
    public static PrepareOptions From(Args a, Defaults d) => new(
        Agents: AtLeastOne(a.RequireInt("--agents")),
        Out: a.Get("--out", d.MachinesFile),
        ServerSettings: a.Get("--server-settings", d.ServerSettings));

    internal static int AtLeastOne(int agents) =>
        agents >= 1 ? agents : throw new ArgumentException("--agents must be at least 1");
}

public sealed record SteadyOptions(string Server, int Agents, TimeSpan Interval, int Minutes, TimeSpan BackoffMax, double LostAckPct, int Seed, string Out)
{
    public static SteadyOptions From(Args a, Defaults d)
    {
        var agents = PrepareOptions.AtLeastOne(a.RequireInt("--agents"));
        return new(
            Server: a.Get("--server", d.Server),
            Agents: agents,
            Interval: Positive("--interval", a.GetDouble("--interval", 60)),
            Minutes: (int)Positive("--minutes", a.GetDouble("--minutes", 10)).TotalSeconds,
            BackoffMax: Positive("--backoff-max", a.GetDouble("--backoff-max", 300)),
            LostAckPct: Percent(a.GetDouble("--lost-ack-pct", 0)),
            Seed: a.GetInt("--seed", 1),
            Out: a.Get("--out", d.ResultPath("steady", agents)));
    }

    internal static TimeSpan Positive(string name, double seconds) =>
        seconds > 0 ? TimeSpan.FromSeconds(seconds) : throw new ArgumentException($"{name} must be positive");

    internal static double Percent(double pct) =>
        pct is >= 0 and <= 100 ? pct : throw new ArgumentException("--lost-ack-pct must be between 0 and 100");
}

public sealed record StormOptions(string Server, int Agents, TimeSpan Backlog, bool Jitter, TimeSpan BackoffMax, TimeSpan Timeout, double LostAckPct, int Seed, string Out)
{
    public static StormOptions From(Args a, Defaults d)
    {
        var agents = PrepareOptions.AtLeastOne(a.RequireInt("--agents"));
        return new(
            Server: a.Get("--server", d.Server),
            Agents: agents,
            Backlog: TimeSpan.FromHours(a.GetDouble("--backlog-hours", 6)) is var b && b > TimeSpan.Zero ? b : throw new ArgumentException("--backlog-hours must be positive"),
            Jitter: !a.Has("--no-jitter"),
            BackoffMax: SteadyOptions.Positive("--backoff-max", a.GetDouble("--backoff-max", 300)),
            Timeout: TimeSpan.FromMinutes(a.GetDouble("--timeout-minutes", 15)),
            LostAckPct: SteadyOptions.Percent(a.GetDouble("--lost-ack-pct", 0)),
            Seed: a.GetInt("--seed", 1),
            Out: a.Get("--out", d.ResultPath("storm", agents)));
    }
}
