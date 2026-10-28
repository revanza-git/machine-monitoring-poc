using System.Text.Json;
using Server.Ingest;
using Shared;

namespace Server.Tests;

public class BatchValidatorTests
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    private static SampleBatch Batch(params long[] seqs) => new(
        MachineId: "WS-DEV-01",
        InstallId: Guid.NewGuid(),
        AgentVersion: "0.1.0",
        Samples: seqs.Select(s => new Sample(s, SampleKinds.CpuMem, DateTimeOffset.UtcNow, Empty)).ToList(),
        Heartbeat: new Heartbeat(null, null, 0, 1, 1, null, 1));

    [Fact]
    public void Empty_heartbeat_only_batch_is_fine()
    {
        Assert.Null(BatchValidator.Validate(Batch()));
    }

    [Fact]
    public void Exactly_the_cap_is_accepted_and_one_more_is_not()
    {
        var atCap = Enumerable.Range(1, SampleBatch.MaxRows).Select(i => (long)i).ToArray();
        Assert.Null(BatchValidator.Validate(Batch(atCap)));

        var overCap = Enumerable.Range(1, SampleBatch.MaxRows + 1).Select(i => (long)i).ToArray();
        Assert.Equal($"at most {SampleBatch.MaxRows} samples per batch", BatchValidator.Validate(Batch(overCap)));
    }

    [Fact]
    public void Seq_must_increase()
    {
        Assert.Null(BatchValidator.Validate(Batch(3, 7, 8)));
        Assert.Equal("seq must be positive and strictly increasing", BatchValidator.Validate(Batch(3, 3)));
        Assert.Equal("seq must be positive and strictly increasing", BatchValidator.Validate(Batch(0)));
    }

    [Fact]
    public void Unknown_kind_is_rejected()
    {
        var batch = Batch(1) with { Samples = [new Sample(1, "gpu", DateTimeOffset.UtcNow, Empty)] };
        Assert.Equal("unknown kind 'gpu'", BatchValidator.Validate(batch));
    }
}
