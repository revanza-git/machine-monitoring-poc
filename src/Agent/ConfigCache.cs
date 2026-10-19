using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// The configuration the agent is running. Starts from the local defaults
/// (version 0), is replaced by whatever the server sends in an ack, and is
/// persisted in the outbox's meta table so the agent starts with the right
/// intervals even when the server is down. The version travels in every
/// heartbeat; the server only sends config when it is behind.
/// </summary>
public sealed class ConfigCache
{
    private const string MetaKey = "config";
    private readonly Outbox _outbox;
    private readonly ILogger<ConfigCache> _logger;
    private readonly int _minIntervalS;

    public AgentConfig Current { get; private set; }

    public ConfigCache(IOptions<AgentOptions> options, Outbox outbox, ILogger<ConfigCache> logger)
    {
        _outbox = outbox;
        _logger = logger;
        _minIntervalS = Math.Max(1, options.Value.CpuTickS);

        var defaults = AgentConfig.Default with
        {
            CpuMemIntervalS = options.Value.CpuMemIntervalS,
            DriveIntervalS = options.Value.DriveIntervalS,
        };

        var cached = outbox.GetMeta(MetaKey);
        Current = cached is null ? defaults : Sanitise(JsonSerializer.Deserialize<AgentConfig>(cached, JsonDefaults.Options) ?? defaults);
        logger.LogInformation("Config v{Version}{Source}: cpu_mem every {CpuMem}s, drives every {Drive}s",
            Current.Version, cached is null ? " (local defaults)" : " (cached)", Current.CpuMemIntervalS, Current.DriveIntervalS);
    }

    /// <summary>Called by the uploader when an ack carries config. Persists first, then switches.</summary>
    public void Apply(AgentConfig config)
    {
        var next = Sanitise(config);
        _outbox.SetMeta(MetaKey, JsonSerializer.Serialize(next, JsonDefaults.Options));
        Current = next;
        _logger.LogInformation("Config v{Version} applied: cpu_mem every {CpuMem}s, drives every {Drive}s, project root {Root}, groups [{Groups}]",
            next.Version, next.CpuMemIntervalS, next.DriveIntervalS, next.ProjectRoot ?? "none", string.Join(", ", next.Groups));
    }

    /// <summary>Never let a server typo make the agent spin: intervals are at least one CPU tick.</summary>
    private AgentConfig Sanitise(AgentConfig c) => c with
    {
        CpuMemIntervalS = Math.Max(_minIntervalS, c.CpuMemIntervalS),
        DriveIntervalS = Math.Max(_minIntervalS, c.DriveIntervalS),
        Groups = c.Groups ?? [],
    };
}
