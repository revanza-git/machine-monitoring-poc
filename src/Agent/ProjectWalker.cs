using Agent.Sampling;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// Sizes the configured project root on its own schedule, separate from the
/// per-minute sampling loop because a walk can take a while. Nothing happens
/// until the server's config names a root. A root change triggers an
/// immediate walk rather than waiting for the next interval, as the design
/// says. The walk runs on a lowest-priority thread so an engineer's build
/// always wins the CPU.
/// </summary>
public sealed class ProjectWalker(IOptions<AgentOptions> options, ConfigCache config, Outbox outbox, LastError lastError, ILogger<ProjectWalker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastRoot = null;
        var nextDue = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));   // how often the root and the schedule are checked

        do
        {
            // A walk that fails (root unreadable, disk full under the outbox)
            // is logged and reported in the heartbeat, then retried on the
            // next schedule. An unhandled exception here would stop the host.
            try
            {
                var root = config.Current.ProjectRoot;
                if (string.IsNullOrWhiteSpace(root))
                {
                    lastRoot = null;
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (root != lastRoot)
                    logger.LogInformation("Project root is {Root}; walking now", root);
                else if (now < nextDue)
                    continue;

                lastRoot = root;
                nextDue = now.AddSeconds(options.Value.ProjectIntervalS);

                if (!Directory.Exists(root))
                {
                    logger.LogWarning("Project root {Root} does not exist on this machine", root);
                    continue;
                }

                var data = await Task.Run(() =>
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                    return ProjectSampler.Walk(root);
                }, ct);

                var seq = outbox.Append(SampleKinds.Project, data.AsOf, data);
                logger.LogInformation("Walked {Root}: {Count} projects, {Bytes:N0} bytes in {Ms} ms, queued as seq {Seq}",
                    root, data.Folders.Count, data.Folders.Sum(f => f.Bytes), data.WalkMs, seq);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Project walk failed");
                lastError.Record("project walk", ex.GetBaseException().Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
