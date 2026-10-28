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
    long? CommitMb,
    long? CommitLimitMb,
    JsonElement? Drives,
    DateTimeOffset? DrivesAt,
    string? ActiveUser,
    string? LastUser,
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

/// <summary>One project across the fleet: headline is the sum of copies, each copy carries its as-of time.</summary>
public sealed record ProjectSummary(
    string Name,
    long TotalBytes,
    bool SizesDiffer,
    IReadOnlyList<ProjectCopy> Copies);

public sealed record ProjectCopy(string Machine, string Root, long Bytes, int Files, DateTimeOffset AsOf);

public sealed record MetricsResponse(
    long MachineId,
    DateTimeOffset From,
    DateTimeOffset To,
    string Resolution,
    IReadOnlyList<MetricPoint> Points);
