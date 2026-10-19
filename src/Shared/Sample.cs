using System.Text.Json;

namespace Shared;

/// <summary>
/// One row of the agent outbox. <see cref="Seq"/> is per install and only
/// increases; together with the install id it makes ingest idempotent.
/// <see cref="Data"/> is kept as raw JSON so the outbox and the ingest
/// endpoint do not need to know every kind.
/// </summary>
public sealed record Sample(
    long Seq,
    string Kind,
    DateTimeOffset SampledAt,
    JsonElement Data);

public static class SampleKinds
{
    public const string CpuMem = "cpu_mem";
    public const string Drive = "drive";
    public const string Project = "project";
}

/// <summary>
/// Payload for <see cref="SampleKinds.Project"/>: one configured root and every
/// first-level folder beneath it with its size. <see cref="AsOf"/> is when the
/// walk finished; every project figure in the portal carries it.
/// </summary>
public sealed record ProjectData(string Root, IReadOnlyList<ProjectEntry> Folders, DateTimeOffset AsOf, long WalkMs);

public sealed record ProjectEntry(string Name, long Bytes, int Files);

/// <summary>Payload for <see cref="SampleKinds.CpuMem"/>. CPU is the average over the interval.</summary>
public sealed record CpuMemData(double CpuPct, long MemUsedMb, long MemTotalMb);

/// <summary>Payload for <see cref="SampleKinds.Drive"/>: every fixed drive at one instant.</summary>
public sealed record DriveData(IReadOnlyList<DriveEntry> Drives);

public sealed record DriveEntry(string Name, long TotalBytes, long FreeBytes);
