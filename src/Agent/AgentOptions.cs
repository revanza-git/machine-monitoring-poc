namespace Agent;

/// <summary>
/// Local settings from appsettings ("Agent" section). Sampling intervals here
/// are the starting point; once the server has replied they come from
/// AgentConfig instead (step 13).
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Reported in the batch for logging. Identity is the API key, not this.</summary>
    public string MachineId { get; set; } = Environment.MachineName;

    /// <summary>Folder for the outbox database. Relative paths resolve against the content root.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>How often the CPU counter is read. The reported value is the average of these reads.</summary>
    public int CpuTickS { get; set; } = 5;

    public int CpuMemIntervalS { get; set; } = 60;

    public int DriveIntervalS { get; set; } = 300;
}
