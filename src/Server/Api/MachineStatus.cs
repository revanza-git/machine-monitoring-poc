namespace Server.Api;

public sealed class StatusOptions
{
    public const string Section = "Status";

    /// <summary>How often agents are expected to post (their upload interval).</summary>
    public int HeartbeatIntervalS { get; set; } = 60;

    /// <summary>Silence for this many intervals marks a machine unresponsive.</summary>
    public int MissedHeartbeatsBeforeUnresponsive { get; set; } = 3;
}

/// <summary>
/// Status with hysteresis, computed at read time from last_seen: a machine
/// goes unresponsive only after three missed heartbeats, and comes back on
/// the very next good one, so a flapping agent shows a steady state rather
/// than a strobe. "off" (clean shutdown with a final heartbeat) arrives with
/// the heartbeat fields in a later step.
/// </summary>
public static class MachineStatus
{
    public const string Online = "online";
    public const string Unresponsive = "unresponsive";
    public const string Unknown = "unknown";   // registered, never reported

    public static string Compute(DateTimeOffset? lastSeen, DateTimeOffset now, StatusOptions o)
    {
        if (lastSeen is null) return Unknown;
        var silence = now - lastSeen.Value;
        var limit = TimeSpan.FromSeconds(o.HeartbeatIntervalS * o.MissedHeartbeatsBeforeUnresponsive);
        return silence > limit ? Unresponsive : Online;
    }
}
