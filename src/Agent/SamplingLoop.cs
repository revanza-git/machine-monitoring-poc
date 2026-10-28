using Agent.Sampling;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// The agent's one timer. Every tick reads the CPU counter; when the cpu_mem
/// or drive interval is due, a sample is closed out and written to the outbox.
/// One wake per tick, as the design's "timers coalesced" line asks. Intervals
/// come from the config cache, so a server-side change takes effect at the
/// next tick without a restart.
/// </summary>
public sealed class SamplingLoop(IOptions<AgentOptions> options, ConfigCache config, Outbox outbox, LastError lastError, ILogger<SamplingLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var cpu = new CpuSampler();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.CpuTickS));

        var now = DateTimeOffset.UtcNow;
        var lastCpuMem = now;                        // needs a full window of ticks first
        var lastDrive = now.AddSeconds(-config.Current.DriveIntervalS);   // drives are a snapshot: take one at once
        var version = config.Current.Version;

        logger.LogInformation("Sampling: cpu tick {Tick}s", options.Value.CpuTickS);

        while (await timer.WaitForNextTickAsync(ct))
        {
            // One bad tick (a counter that disappears, a full disk under the
            // outbox) is logged and reported in the heartbeat; the next tick
            // runs as usual. An unhandled exception here would stop the host.
            try
            {
                now = DateTimeOffset.UtcNow;
                cpu.Tick();

                var cfg = config.Current;
                if (cfg.Version != version)
                {
                    logger.LogInformation("Sampling now follows config v{Version}", cfg.Version);
                    version = cfg.Version;
                }

                if (now >= lastDrive.AddSeconds(cfg.DriveIntervalS))
                {
                    Queue(SampleKinds.Drive, now, DriveSampler.Read());
                    lastDrive = now;
                }

                if (now >= lastCpuMem.AddSeconds(cfg.CpuMemIntervalS) && cpu.CloseInterval() is { } cpuPct)
                {
                    var mem = MemorySampler.Read();
                    Queue(SampleKinds.CpuMem, now, new CpuMemData(cpuPct, mem.UsedMb, mem.TotalMb, mem.CommitMb, mem.CommitLimitMb));
                    lastCpuMem = now;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sampling tick failed");
                lastError.Record("sampling", ex.GetBaseException().Message);
            }
        }
    }

    private void Queue(string kind, DateTimeOffset sampledAt, object data)
    {
        var seq = outbox.Append(kind, sampledAt, data);
        logger.LogInformation("Queued {Kind} as seq {Seq} (outbox depth {Depth})", kind, seq, outbox.Depth());
    }
}
