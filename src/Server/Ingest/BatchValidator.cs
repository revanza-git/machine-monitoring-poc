using Shared;

namespace Server.Ingest;

/// <summary>Shape checks only. Returns a reason string, or null when the batch is acceptable.</summary>
public static class BatchValidator
{
    public static string? Validate(SampleBatch batch)
    {
        if (string.IsNullOrWhiteSpace(batch.MachineId)) return "machine_id is required";
        if (batch.InstallId == Guid.Empty) return "install_id is required";
        if (batch.Samples is null || batch.Heartbeat is null) return "samples and heartbeat are required";
        if (batch.Samples.Count > SampleBatch.MaxRows) return $"at most {SampleBatch.MaxRows} samples per batch";

        long previous = 0;
        foreach (var s in batch.Samples)
        {
            if (s.Seq <= previous) return "seq must be positive and strictly increasing";
            if (s.Kind is not (SampleKinds.CpuMem or SampleKinds.Drive or SampleKinds.Project)) return $"unknown kind '{s.Kind}'";
            if (s.SampledAt == default) return "sampled_at is required";
            previous = s.Seq;
        }
        return null;
    }
}
