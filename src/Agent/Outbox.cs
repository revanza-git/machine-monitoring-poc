using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// Store-and-forward buffer on local disk. Every sample is written here first
/// and deleted only after the server acknowledges its seq.
///
/// seq is INTEGER PRIMARY KEY AUTOINCREMENT: SQLite guarantees it is never
/// reused, even after rows are deleted, so the per-install sequence only
/// increases. The install id lives in the same file (meta table) so sequence
/// and install id can never drift apart: delete the file and both reset
/// together, which is exactly what the server's idempotency key needs.
///
/// WAL mode plus synchronous=FULL makes a write durable once Append returns.
/// At one write a minute that fsync costs nothing worth saving.
/// </summary>
public sealed class Outbox : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();   // Microsoft.Data.Sqlite connections are not thread-safe

    public Guid InstallId { get; }
    public string Path { get; }

    public Outbox(IOptions<AgentOptions> options, IHostEnvironment env)
    {
        var dir = System.IO.Path.GetFullPath(options.Value.DataDir, env.ContentRootPath);
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "outbox.db");

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=FULL");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox (
                seq        INTEGER PRIMARY KEY AUTOINCREMENT,
                kind       TEXT NOT NULL,
                sampled_at TEXT NOT NULL,
                data       TEXT NOT NULL);
            """);
        InstallId = LoadOrCreateInstallId();
    }

    /// <summary>Durably queue one sample. Returns its seq.</summary>
    public long Append(string kind, DateTimeOffset sampledAt, object data)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO outbox (kind, sampled_at, data) VALUES ($kind, $at, $data); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$at", sampledAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data, JsonDefaults.Options));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Oldest unacknowledged rows, in seq order, at most <paramref name="max"/>.</summary>
    public List<Sample> PeekBatch(int max)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT seq, kind, sampled_at, data FROM outbox ORDER BY seq LIMIT $max";
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            var rows = new List<Sample>();
            while (reader.Read())
            {
                rows.Add(new Sample(
                    Seq: reader.GetInt64(0),
                    Kind: reader.GetString(1),
                    SampledAt: DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    Data: JsonDocument.Parse(reader.GetString(3)).RootElement.Clone()));
            }
            return rows;
        }
    }

    /// <summary>Called only after the server acknowledged <paramref name="ackThrough"/>.</summary>
    public int DeleteThrough(long ackThrough)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM outbox WHERE seq <= $seq";
            cmd.Parameters.AddWithValue("$seq", ackThrough);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Enforces the outbox age cap. sampled_at is stored in round-trip ("O")
    /// UTC format, which sorts as text in time order, so a string comparison is enough.
    /// </summary>
    public int DeleteOlderThan(TimeSpan maxAge)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM outbox WHERE sampled_at < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - maxAge).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Rows waiting to be sent. Reported in the heartbeat.</summary>
    public long Depth()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Small key/value store beside the queue: install id, restart count, cached config.</summary>
    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Called once per process start, so this is a lifetime count of starts:
    /// reboots and deliberate service restarts raise it too. The restart-loop
    /// signal is a count that climbs between consecutive heartbeats, which
    /// the PoC leaves to whoever reads the portal; production would reset the
    /// counter after an hour of steady running and flag the loop server-side.
    /// </summary>
    public int IncrementRestartCount()
    {
        var count = int.Parse(GetMeta("restart_count") ?? "0", CultureInfo.InvariantCulture) + 1;
        SetMeta("restart_count", count.ToString(CultureInfo.InvariantCulture));
        return count;
    }

    private Guid LoadOrCreateInstallId()
    {
        if (GetMeta("install_id") is { } existing)
            return Guid.Parse(existing);

        var id = Guid.NewGuid();
        SetMeta("install_id", id.ToString());
        SetMeta("created_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return id;
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
