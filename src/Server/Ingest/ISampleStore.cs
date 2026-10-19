using Server.Auth;
using Shared;

namespace Server.Ingest;

/// <summary>
/// Where accepted batches go. Implementations must be idempotent on
/// (machine, install_id, seq) and return the highest seq they hold for that
/// install, which becomes ack_through.
/// </summary>
public interface ISampleStore
{
    Task<long> StoreAsync(MachineIdentity machine, SampleBatch batch, DateTimeOffset receivedAt, CancellationToken ct);
}
