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

    /// <summary>Base URL of the central server. The agent only ever connects outbound to this.</summary>
    public string ServerUrl { get; set; } = "https://localhost:7181";

    /// <summary>Per-machine credential sent as X-Api-Key. PoC stand-in for the machine certificate.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Development only: accept the server certificate without validation, for
    /// machines where the ASP.NET dev certificate is not trusted. Never in production.
    /// </summary>
    public bool TrustAnyServerCertificate { get; set; }

    /// <summary>How often the CPU counter is read. The reported value is the average of these reads.</summary>
    public int CpuTickS { get; set; } = 5;

    /// <summary>Sampling intervals used until the server has sent config (then ConfigCache wins).</summary>
    public int CpuMemIntervalS { get; set; } = 60;

    public int DriveIntervalS { get; set; } = 300;

    /// <summary>How often the project root (if the server configured one) is walked. Nightly in the design.</summary>
    public int ProjectIntervalS { get; set; } = 86400;

    /// <summary>
    /// Local hour of the walk when ProjectIntervalS is a day or more, so it
    /// lands in off-hours. Shorter intervals (Development profile) ignore it.
    /// </summary>
    public int ProjectWalkHour { get; set; } = 2;

    /// <summary>How often the outbox is drained. Also the heartbeat cadence.</summary>
    public int UploadIntervalS { get; set; } = 60;

    /// <summary>Rows per POST. The design caps batches at 500.</summary>
    public int MaxBatchRows { get; set; } = 500;

    /// <summary>First retry delay after a failed upload. Doubles per failure up to BackoffMaxS, with jitter.</summary>
    public int BackoffInitialS { get; set; } = 5;

    public int BackoffMaxS { get; set; } = 300;

    /// <summary>"No data loss" holds for outages shorter than this; older rows are dropped, oldest first.</summary>
    public int OutboxMaxAgeDays { get; set; } = 7;
}
