using Agent.Sampling;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// Sizes the configured project root on its own schedule, separate from the
/// per-minute sampling loop because a walk can take a while. Nothing happens
/// until the server's config names a root. A root change triggers an
/// immediate walk rather than waiting for the next interval, as the design
/// says; after that the walk is nightly at <see cref="AgentOptions.ProjectWalkHour"/>
/// local time. It runs on an <see cref="IdleThread"/>: lowest CPU priority
/// and background I/O priority, so an engineer's build always wins.
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
                nextDue = NextDue(now, options.Value.ProjectIntervalS, options.Value.ProjectWalkHour, TimeZoneInfo.Local);

                if (!Directory.Exists(root))
                {
                    logger.LogWarning("Project root {Root} does not exist on this machine", root);
                    continue;
                }

                var data = await IdleThread.Run(() => ProjectSampler.Walk(root), "project walk");

                var seq = outbox.Append(SampleKinds.Project, data.AsOf, data);
                logger.LogInformation("Walked {Root}: {Count} projects, {Bytes:N0} bytes in {Ms} ms, queued as seq {Seq}; next walk {Next:yyyy-MM-dd HH:mm}",
                    root, data.Folders.Count, data.Folders.Sum(f => f.Bytes), data.WalkMs, seq, nextDue.ToLocalTime());
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Project walk failed");
                lastError.Record("project walk", ex.GetBaseException().Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>
    /// An interval of a day or more means "nightly": the next occurrence of
    /// <paramref name="walkHour"/> in <paramref name="zone"/>, so the walk lands
    /// in off-hours instead of 24 hours after whenever the agent happened to
    /// start. Shorter intervals (the Development profile) simply repeat.
    /// </summary>
    public static DateTimeOffset NextDue(DateTimeOffset now, int intervalS, int walkHour, TimeZoneInfo zone)
    {
        if (intervalS < 86400)
            return now.AddSeconds(intervalS);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date.AddHours(walkHour);
        if (candidate <= local.DateTime)
            candidate = candidate.AddDays(1);
        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }
}
