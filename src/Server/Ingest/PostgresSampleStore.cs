using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Server.Auth;
using Shared;

namespace Server.Ingest;

/// <summary>
/// Persists a batch in ONE transaction: upsert the machines row, append
/// history rows (ON CONFLICT DO NOTHING on the (machine, install, seq) key,
/// so resends are dropped), and upsert machine_latest from the heartbeat and
/// the newest sample of each kind. Overview and charts therefore can never
/// disagree, and a lost ack costs a harmless resend, never a double count.
///
/// History inserts go in one NpgsqlBatch: a 500-row batch is one round trip.
/// A sample whose payload does not parse is logged and acknowledged rather
/// than rejected, so one poison row cannot wedge an agent into retrying it
/// forever. Production would quarantine it; the PoC drops it.
/// </summary>
public sealed class PostgresSampleStore(NpgsqlDataSource db, ILogger<PostgresSampleStore> logger) : ISampleStore
{
    public async Task<long> StoreAsync(MachineIdentity machine, SampleBatch batch, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var machineId = await UpsertMachineAsync(conn, machine.Name, batch.MachineId, ct);

        var (inserted, latestCpuMem, latestDrives) = await InsertHistoryAsync(conn, machineId, batch, receivedAt, ct);
        await UpsertLatestAsync(conn, machineId, batch, receivedAt, latestCpuMem, latestDrives, ct);

        await tx.CommitAsync(ct);

        // Everything at or below the batch's highest seq is now stored, or was stored by an earlier batch.
        var ackThrough = batch.Samples.Count == 0 ? 0 : batch.Samples[^1].Seq;
        logger.LogInformation("Inserted {Inserted} history rows from {Total} samples for {Machine}, ack_through {Ack}",
            inserted, batch.Samples.Count, machine.Name, ackThrough);
        return ackThrough;
    }

    private static async Task<long> UpsertMachineAsync(NpgsqlConnection conn, string name, string hostname, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO machines (name, hostname) VALUES ($1, $2)
            ON CONFLICT (name) DO UPDATE SET hostname = EXCLUDED.hostname
            RETURNING machine_id
            """, conn);
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = hostname });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<(int Inserted, (Sample Sample, CpuMemData Data)? Cpu, (Sample Sample, DriveData Data)? Drives)>
        InsertHistoryAsync(NpgsqlConnection conn, long machineId, SampleBatch batch, DateTimeOffset receivedAt, CancellationToken ct)
    {
        (Sample, CpuMemData)? latestCpu = null;
        (Sample, DriveData)? latestDrives = null;

        await using var history = new NpgsqlBatch(conn);
        foreach (var s in batch.Samples)
        {
            switch (s.Kind)
            {
                case SampleKinds.CpuMem when TryParse<CpuMemData>(s, out var cpu):
                    history.BatchCommands.Add(CpuMemInsert(machineId, batch.InstallId, s, receivedAt, cpu));
                    latestCpu = (s, cpu);   // samples arrive in seq order, so the last one is the newest
                    break;

                case SampleKinds.Drive when TryParse<DriveData>(s, out var drives):
                    foreach (var d in drives.Drives)
                        history.BatchCommands.Add(DriveInsert(machineId, batch.InstallId, s, receivedAt, d));
                    latestDrives = (s, drives);
                    break;
            }
        }

        var inserted = history.BatchCommands.Count == 0 ? 0 : await history.ExecuteNonQueryAsync(ct);
        return (inserted, latestCpu, latestDrives);
    }

    private bool TryParse<T>(Sample s, out T data)
    {
        try
        {
            data = s.Data.Deserialize<T>(JsonDefaults.Options)!;
            return data is not null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Dropping seq {Seq} ({Kind}): payload does not parse: {Reason}", s.Seq, s.Kind, ex.Message);
            data = default!;
            return false;
        }
    }

    private static NpgsqlBatchCommand CpuMemInsert(long machineId, Guid installId, Sample s, DateTimeOffset receivedAt, CpuMemData d)
    {
        var cmd = new NpgsqlBatchCommand("""
            INSERT INTO cpu_mem_samples (time, machine_id, install_id, seq, received_at, cpu_pct, mem_used_mb, mem_total_mb)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.Add(new() { Value = s.SampledAt.UtcDateTime });
        cmd.Parameters.Add(new() { Value = machineId });
        cmd.Parameters.Add(new() { Value = installId });
        cmd.Parameters.Add(new() { Value = s.Seq });
        cmd.Parameters.Add(new() { Value = receivedAt.UtcDateTime });
        cmd.Parameters.Add(new() { Value = d.CpuPct });
        cmd.Parameters.Add(new() { Value = d.MemUsedMb });
        cmd.Parameters.Add(new() { Value = d.MemTotalMb });
        return cmd;
    }

    private static NpgsqlBatchCommand DriveInsert(long machineId, Guid installId, Sample s, DateTimeOffset receivedAt, DriveEntry d)
    {
        var cmd = new NpgsqlBatchCommand("""
            INSERT INTO drive_samples (time, machine_id, install_id, seq, received_at, drive, total_bytes, free_bytes)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.Add(new() { Value = s.SampledAt.UtcDateTime });
        cmd.Parameters.Add(new() { Value = machineId });
        cmd.Parameters.Add(new() { Value = installId });
        cmd.Parameters.Add(new() { Value = s.Seq });
        cmd.Parameters.Add(new() { Value = receivedAt.UtcDateTime });
        cmd.Parameters.Add(new() { Value = d.Name });
        cmd.Parameters.Add(new() { Value = d.TotalBytes });
        cmd.Parameters.Add(new() { Value = d.FreeBytes });
        return cmd;
    }

    /// <summary>
    /// Heartbeat fields are always refreshed. Sample fields are refreshed only
    /// when this batch carried that kind (COALESCE keeps the previous value
    /// otherwise), so a heartbeat-only post never blanks the overview.
    /// </summary>
    private static async Task UpsertLatestAsync(
        NpgsqlConnection conn, long machineId, SampleBatch batch, DateTimeOffset receivedAt,
        (Sample Sample, CpuMemData Data)? cpu, (Sample Sample, DriveData Data)? drives, CancellationToken ct)
    {
        var hb = batch.Heartbeat;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO machine_latest (
                machine_id, last_seen, active_user, agent_version, install_id,
                outbox_depth, restart_count, last_error, config_version,
                sampled_at, cpu_pct, mem_used_mb, mem_total_mb, drives, drives_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            ON CONFLICT (machine_id) DO UPDATE SET
                last_seen      = EXCLUDED.last_seen,
                active_user    = EXCLUDED.active_user,
                agent_version  = EXCLUDED.agent_version,
                install_id     = EXCLUDED.install_id,
                outbox_depth   = EXCLUDED.outbox_depth,
                restart_count  = EXCLUDED.restart_count,
                last_error     = EXCLUDED.last_error,
                config_version = EXCLUDED.config_version,
                sampled_at     = COALESCE(EXCLUDED.sampled_at,   machine_latest.sampled_at),
                cpu_pct        = COALESCE(EXCLUDED.cpu_pct,      machine_latest.cpu_pct),
                mem_used_mb    = COALESCE(EXCLUDED.mem_used_mb,  machine_latest.mem_used_mb),
                mem_total_mb   = COALESCE(EXCLUDED.mem_total_mb, machine_latest.mem_total_mb),
                drives         = COALESCE(EXCLUDED.drives,       machine_latest.drives),
                drives_at      = COALESCE(EXCLUDED.drives_at,    machine_latest.drives_at)
            """, conn);

        cmd.Parameters.Add(new() { Value = machineId });
        cmd.Parameters.Add(new() { Value = receivedAt.UtcDateTime });
        cmd.Parameters.Add(new() { Value = (object?)hb.ActiveUser ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = batch.AgentVersion });
        cmd.Parameters.Add(new() { Value = batch.InstallId });
        cmd.Parameters.Add(new() { Value = hb.OutboxDepth });
        cmd.Parameters.Add(new() { Value = hb.RestartCount });
        cmd.Parameters.Add(new() { Value = (object?)hb.LastError ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = hb.ConfigVersion });
        cmd.Parameters.Add(new() { Value = cpu is null ? DBNull.Value : cpu.Value.Sample.SampledAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = cpu is null ? DBNull.Value : cpu.Value.Data.CpuPct, NpgsqlDbType = NpgsqlDbType.Double });
        cmd.Parameters.Add(new() { Value = cpu is null ? DBNull.Value : cpu.Value.Data.MemUsedMb, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = cpu is null ? DBNull.Value : cpu.Value.Data.MemTotalMb, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = drives is null ? DBNull.Value : JsonSerializer.Serialize(drives.Value.Data.Drives, JsonDefaults.Options), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = drives is null ? DBNull.Value : drives.Value.Sample.SampledAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
