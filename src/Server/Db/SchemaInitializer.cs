using System.Reflection;
using Npgsql;

namespace Server.Db;

/// <summary>
/// Applies Db/schema.sql (embedded in the assembly) on startup. The script is
/// idempotent, so running it on every start is the PoC's whole migration
/// story. Retries for a while because "docker compose up -d" returns before
/// PostgreSQL accepts connections.
/// </summary>
public static class SchemaInitializer
{
    public static async Task ApplyAsync(NpgsqlDataSource db, ILogger logger, CancellationToken ct)
    {
        var sql = ReadEmbedded("Server.Db.schema.sql");
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (true)
        {
            try
            {
                await using var cmd = db.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync(ct);
                logger.LogInformation("Schema applied");
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException && DateTime.UtcNow < deadline)
            {
                logger.LogWarning("Database not ready ({Reason}); retrying", ex.Message.Split('\n')[0]);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"embedded resource {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
