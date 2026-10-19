using Agent.Sampling;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// The agent's one timer. Every tick reads the CPU counter; when the cpu_mem
/// or drive interval is due, a sample is closed out and handed to the sink.
/// One wake per tick, as the design's "timers coalesced" line asks.
/// </summary>
public sealed class SamplingLoop(IOptions<AgentOptions> options, ISampleSink sink, ILogger<SamplingLoop> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        using var cpu = new CpuSampler();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.CpuTickS));

        var now = DateTimeOffset.UtcNow;
        var nextCpuMem = now.AddSeconds(o.CpuMemIntervalS);   // needs a full window of ticks first
        var nextDrive = now;                                   // drives are a snapshot, so take one at once

        logger.LogInformation("Sampling: cpu tick {Tick}s, cpu_mem every {CpuMem}s, drives every {Drive}s",
            o.CpuTickS, o.CpuMemIntervalS, o.DriveIntervalS);

        while (await timer.WaitForNextTickAsync(ct))
        {
            now = DateTimeOffset.UtcNow;
            cpu.Tick();

            if (now >= nextDrive)
            {
                await sink.WriteAsync(SampleKinds.Drive, now, DriveSampler.Read(), ct);
                nextDrive = now.AddSeconds(o.DriveIntervalS);
            }

            if (now >= nextCpuMem && cpu.CloseInterval() is { } cpuPct)
            {
                var mem = MemorySampler.Read();
                await sink.WriteAsync(SampleKinds.CpuMem, now, new CpuMemData(cpuPct, mem.UsedMb, mem.TotalMb), ct);
                nextCpuMem = now.AddSeconds(o.CpuMemIntervalS);
            }
        }
    }
}
