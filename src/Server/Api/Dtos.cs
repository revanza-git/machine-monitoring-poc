using System.Text.Json;

namespace Server.Api;

/// <summary>One row of the fleet overview: machines joined to machine_latest.</summary>
public sealed record MachineSummary(
    long MachineId,
    string Name,
    string? Hostname,
    string Status,
    DateTimeOffset? LastSeen,
    DateTimeOffset? SampledAt,
    double? CpuPct,
    long? MemUsedMb,
    long? MemTotalMb,
    JsonElement? Drives,
    DateTimeOffset? DrivesAt,
    string? ActiveUser,
    string? AgentVersion,
    long? OutboxDepth,
    int? RestartCount,
    string? LastError,
    int? ConfigVersion);

/// <summary>One chart point. For raw resolution min = avg = max.</summary>
public sealed record MetricPoint(
    DateTimeOffset Time,
    double CpuAvg,
    double CpuMin,
    double CpuMax,
    long MemUsedMb,
    long MemTotalMb);

public sealed record MetricsResponse(
    long MachineId,
    DateTimeOffset From,
    DateTimeOffset To,
    string Resolution,
    IReadOnlyList<MetricPoint> Points);
