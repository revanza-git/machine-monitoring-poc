namespace Server.Api;

public sealed class StatusOptions
{
    public const string Section = "Status";

    /// <summary>How often agents are expected to post (their upload interval).</summary>
    public int HeartbeatIntervalS { get; set; } = 60;

    /// <summary>Silence for this many intervals marks a machine unresponsive.</summary>
    public int MissedHeartbeatsBeforeUnresponsive { get; set; } = 3;

    /// <summary>
    /// This many agent starts inside an hour flag a restart loop. The service
    /// is installed with "restart twice, then stop", so a third start within
    /// the hour means the recovery actions are used up and the crash persists.
    /// </summary>
    public int RestartLoopStarts { get; set; } = 3;
}

/// <summary>
/// Status with hysteresis, computed at read time from last_seen: a machine
/// goes unresponsive only after three missed heartbeats, and comes back on
/// the very next good one, so a flapping agent shows a steady state rather
/// than a strobe. A machine whose last heartbeat was the goodbye of a clean
/// shutdown is "off"; silence without that goodbye is "unresponsive".
/// </summary>
public static class MachineStatus
{
    public const string Online = "online";
    public const string Off = "off";
    public const string Unresponsive = "unresponsive";
    public const string Unknown = "unknown";   // registered, never reported

    public static string Compute(DateTimeOffset? lastSeen, DateTimeOffset? stoppedAt, DateTimeOffset now, StatusOptions o)
    {
        if (lastSeen is null) return Unknown;
        if (stoppedAt is not null && stoppedAt >= lastSeen) return Off;
        var silence = now - lastSeen.Value;
        var limit = TimeSpan.FromSeconds(o.HeartbeatIntervalS * o.MissedHeartbeatsBeforeUnresponsive);
        return silence > limit ? Unresponsive : Online;
    }

    /// <summary>
    /// The restart-loop flag, from the agent's count of starts in the last
    /// hour. Orthogonal to status: a looping agent is usually "online",
    /// because each short life manages a heartbeat before it dies.
    /// </summary>
    public static bool IsRestartLoop(int? restartsLastHour, StatusOptions o) =>
        restartsLastHour is { } n && n >= o.RestartLoopStarts;
}
