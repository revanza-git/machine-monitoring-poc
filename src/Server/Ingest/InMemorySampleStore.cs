using System.Collections.Concurrent;
using Server.Auth;
using Shared;

namespace Server.Ingest;

/// <summary>
/// Placeholder until PostgreSQL lands: remembers the highest seq per
/// (machine, install) so resends are dropped and ack_through is monotonic.
/// </summary>
public sealed class InMemorySampleStore(ILogger<InMemorySampleStore> logger) : ISampleStore
{
    private readonly ConcurrentDictionary<(string Machine, Guid Install), long> _highestSeq = new();

    public Task<long> StoreAsync(MachineIdentity machine, SampleBatch batch, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var key = (machine.Name, batch.InstallId);
        var before = _highestSeq.GetValueOrDefault(key);
        var fresh = batch.Samples.Count(s => s.Seq > before);
        var after = batch.Samples.Count == 0 ? before : Math.Max(before, batch.Samples[^1].Seq);
        _highestSeq[key] = after;

        logger.LogInformation("Stored {Fresh} new of {Total} samples for {Machine}, ack_through {Ack}",
            fresh, batch.Samples.Count, machine.Name, after);
        return Task.FromResult(after);
    }
}
