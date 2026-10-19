namespace Shared;

/// <summary>
/// Agent health that rides along with every upload, so the portal doubles as
/// the fleet health view. <see cref="ConfigVersion"/> is the version the agent
/// is currently running; the server sends config back only when it differs.
/// <see cref="Stopping"/> is true on the final heartbeat of a clean shutdown,
/// which is how the portal tells "off" from "unresponsive".
/// </summary>
public sealed record Heartbeat(
    string? ActiveUser,
    long OutboxDepth,
    int RestartCount,
    string? LastError,
    int ConfigVersion,
    bool Stopping = false);
