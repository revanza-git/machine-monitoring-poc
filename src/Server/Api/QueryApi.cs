using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Server.Api;

/// <summary>
/// Read-only API for the portal. Reads the database and nothing else.
/// GET /api/machines            fleet overview from machine_latest (one query, never scans history)
/// GET /api/machines/{id}/metrics?from&amp;to   cpu/memory points at a resolution chosen from the range
/// </summary>
public static class QueryApi
{
    public static IEndpointRouteBuilder MapQueryApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/machines", ListMachinesAsync);
        app.MapGet("/api/machines/{id:long}/metrics", MetricsAsync);
        app.MapGet("/api/projects", ProjectsAsync);
        return app;
    }

    /// <summary>
    /// Newest walk per (machine, root), grouped by project name. Every walk is
    /// a full snapshot and all its rows share one time, so a folder missing
    /// from the newest walk has been renamed or deleted and drops out of the
    /// view instead of lingering with an ever-older as-of time.
    /// Totals are sums of copies with no cross-machine deduplication, as the
    /// design states; copies whose sizes differ are flagged.
    /// </summary>
    private static async Task<IResult> ProjectsAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.project_name, m.name, l.root, l.bytes, l.files, l.as_of
            FROM (
                SELECT machine_id, root, max(time) AS time
                FROM project_sizes
                GROUP BY machine_id, root
            ) newest
            JOIN project_sizes l USING (machine_id, root, time)
            JOIN machines m USING (machine_id)
            ORDER BY l.project_name, m.name
            """);

        var copies = new List<(string Project, ProjectCopy Copy)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            copies.Add((r.GetString(0), new ProjectCopy(r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetTime(5))));

        var projects = copies
            .GroupBy(c => c.Project)
            .Select(g => new ProjectSummary(
                Name: g.Key,
                TotalBytes: g.Sum(c => c.Copy.Bytes),
                SizesDiffer: g.Select(c => c.Copy.Bytes).Distinct().Count() > 1,
                Copies: g.Select(c => c.Copy).ToList()))
            .OrderByDescending(p => p.TotalBytes)
            .ToList();

        return Results.Ok(projects);
    }

    private static async Task<IResult> ListMachinesAsync(
        NpgsqlDataSource db, IOptions<StatusOptions> status, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT m.machine_id, m.name, m.hostname,
                   l.last_seen, l.sampled_at, l.cpu_pct, l.mem_used_mb, l.mem_total_mb,
                   l.drives::text, l.drives_at, l.active_user, l.agent_version,
                   l.outbox_depth, l.restart_count, l.last_error, l.config_version, l.stopped_at, l.last_user
            FROM machines m
            LEFT JOIN machine_latest l USING (machine_id)
            ORDER BY m.name
            """);

        var now = DateTimeOffset.UtcNow;
        var rows = new List<MachineSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var lastSeen = r.GetNullableTime(3);
            var stoppedAt = r.GetNullableTime(16);
            rows.Add(new MachineSummary(
                MachineId: r.GetInt64(0),
                Name: r.GetString(1),
                Hostname: r.GetNullableString(2),
                Status: MachineStatus.Compute(lastSeen, stoppedAt, now, status.Value),
                LastSeen: lastSeen,
                SampledAt: r.GetNullableTime(4),
                CpuPct: r.GetNullable<double>(5),
                MemUsedMb: r.GetNullable<long>(6),
                MemTotalMb: r.GetNullable<long>(7),
                Drives: r.IsDBNull(8) ? null : JsonDocument.Parse(r.GetString(8)).RootElement.Clone(),
                DrivesAt: r.GetNullableTime(9),
                ActiveUser: r.GetNullableString(10),
                LastUser: r.GetNullableString(17),
                AgentVersion: r.GetNullableString(11),
                OutboxDepth: r.GetNullable<long>(12),
                RestartCount: r.GetNullable<int>(13),
                LastError: r.GetNullableString(14),
                ConfigVersion: r.GetNullable<int>(15)));
        }
        return Results.Ok(rows);
    }

    /// <summary>
    /// Resolution follows <see cref="MetricsResolution"/>: raw up to an hour,
    /// 5-minute buckets up to a week, hourly beyond, so a chart never reads
    /// more than a few thousand rows. 5-minute buckets are computed on the
    /// fly; the hourly path reads the cpu_mem_hourly continuous aggregate,
    /// which TimescaleDB keeps current as data arrives.
    /// </summary>
    private static async Task<IResult> MetricsAsync(
        long id, DateTimeOffset? from, DateTimeOffset? to, NpgsqlDataSource db, CancellationToken ct)
    {
        var toUtc = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var fromUtc = (from ?? toUtc.AddHours(-1)).ToUniversalTime();
        if (fromUtc >= toUtc) return Results.BadRequest(new { error = "from must be before to" });

        var (resolution, bucket) = MetricsResolution.Pick(toUtc - fromUtc);

        await using var cmd = resolution switch
        {
            MetricsResolution.Raw => db.CreateCommand("""
                SELECT time, cpu_pct, cpu_pct, cpu_pct, mem_used_mb, mem_total_mb
                FROM cpu_mem_samples
                WHERE machine_id = $1 AND time >= $2 AND time < $3
                ORDER BY time
                """),
            MetricsResolution.FiveMinute => db.CreateCommand("""
                SELECT time_bucket($4, time) AS bucket,
                       avg(cpu_pct), min(cpu_pct), max(cpu_pct),
                       avg(mem_used_mb)::bigint, max(mem_total_mb)
                FROM cpu_mem_samples
                WHERE machine_id = $1 AND time >= $2 AND time < $3
                GROUP BY bucket
                ORDER BY bucket
                """),
            _ => db.CreateCommand("""
                SELECT bucket, cpu_avg, cpu_min, cpu_max, mem_used_avg, mem_total_max
                FROM cpu_mem_hourly
                WHERE machine_id = $1 AND bucket >= $2 AND bucket < $3
                ORDER BY bucket
                """),
        };

        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = fromUtc.UtcDateTime });
        cmd.Parameters.Add(new() { Value = toUtc.UtcDateTime });
        if (resolution == MetricsResolution.FiveMinute) cmd.Parameters.Add(new() { Value = bucket!.Value });

        var points = new List<MetricPoint>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            points.Add(new MetricPoint(
                Time: r.GetTime(0),
                CpuAvg: r.GetDouble(1),
                CpuMin: r.GetDouble(2),
                CpuMax: r.GetDouble(3),
                MemUsedMb: r.GetInt64(4),
                MemTotalMb: r.GetInt64(5)));
        }

        return Results.Ok(new MetricsResponse(id, fromUtc, toUtc, resolution, points));
    }
}

/// <summary>Small readers so the query code above stays readable without an ORM.</summary>
internal static class ReaderExtensions
{
    // Two overloads on purpose: without the struct constraint, "T?" on a value
    // type is not Nullable<T> and a NULL would come back as 0.
    public static T? GetNullable<T>(this NpgsqlDataReader r, int i) where T : struct =>
        r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);

    public static string? GetNullableString(this NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>timestamptz comes back as a UTC DateTime; expose it as DateTimeOffset.</summary>
    public static DateTimeOffset GetTime(this NpgsqlDataReader r, int i) =>
        new(DateTime.SpecifyKind(r.GetFieldValue<DateTime>(i), DateTimeKind.Utc));

    public static DateTimeOffset? GetNullableTime(this NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetTime(i);
}
