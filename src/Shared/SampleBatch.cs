namespace Shared;

/// <summary>
/// Body of <c>POST /v1/samples</c>. <see cref="MachineId"/> is for logging
/// only: the server takes identity from the credential, never the body.
/// <see cref="InstallId"/> is generated once per installation so a re-imaged
/// machine restarting at seq 1 cannot collide with its own history.
/// </summary>
public sealed record SampleBatch(
    string MachineId,
    Guid InstallId,
    string AgentVersion,
    IReadOnlyList<Sample> Samples,
    Heartbeat Heartbeat);
