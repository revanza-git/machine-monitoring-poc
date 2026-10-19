using Shared;

namespace Server.Config;

/// <summary>
/// The configuration the server hands to agents. The design keeps this per
/// machine or group in the admin area; the PoC keeps one fleet-wide copy in
/// appsettings ("AgentConfig" section). It is read through IOptionsMonitor,
/// so editing the file while the server runs changes what the next heartbeat
/// reply carries. Bump Version whenever a value changes: agents compare
/// versions, not values.
/// </summary>
public sealed class AgentConfigOptions
{
    public const string Section = "AgentConfig";

    public int Version { get; set; } = 1;
    public int CpuMemIntervalS { get; set; } = 60;
    public int DriveIntervalS { get; set; } = 300;
    public string? ProjectRoot { get; set; }
    public List<string> Groups { get; set; } = [];

    public AgentConfig ToContract() => new(Version, CpuMemIntervalS, DriveIntervalS, ProjectRoot, Groups);
}
