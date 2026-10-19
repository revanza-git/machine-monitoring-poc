namespace Shared;

/// <summary>
/// Everything the server decides for an agent. Versioned so the agent can
/// report what it runs and the server can skip sending unchanged config.
/// </summary>
public sealed record AgentConfig(
    int Version,
    int CpuMemIntervalS,
    int DriveIntervalS,
    string? ProjectRoot,
    IReadOnlyList<string> Groups)
{
    /// <summary>What an agent runs before it has ever heard from the server.</summary>
    public static readonly AgentConfig Default = new(
        Version: 0,
        CpuMemIntervalS: 60,
        DriveIntervalS: 300,
        ProjectRoot: null,
        Groups: []);
}
