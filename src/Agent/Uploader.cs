using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// Drains the outbox to the server. On each interval it peeks the oldest
/// rows (at most <see cref="AgentOptions.MaxBatchRows"/>), posts them with a
/// heartbeat, and deletes rows only after the server's ack_through covers
/// them. A full batch is followed immediately by the next one so a backlog
/// drains in capped batches rather than one per interval. A heartbeat-only
/// post still goes out when the outbox is empty: that is how the server
/// knows the machine is alive.
/// </summary>
public sealed class Uploader(IOptions<AgentOptions> options, Outbox outbox, ILogger<Uploader> logger)
    : BackgroundService
{
    private static readonly string AgentVersion =
        typeof(Uploader).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly HttpClient _http = CreateClient(options.Value);

    private static HttpClient CreateClient(AgentOptions o)
    {
        var handler = new HttpClientHandler();
        if (o.TrustAnyServerCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(o.ServerUrl),
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { { "X-Api-Key", o.ApiKey } },
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        logger.LogInformation("Uploading to {Server} every {Interval}s, up to {Max} rows per batch",
            o.ServerUrl, o.UploadIntervalS, o.MaxBatchRows);
        if (o.TrustAnyServerCertificate)
            logger.LogWarning("TrustAnyServerCertificate is on: server certificate is NOT validated (development only)");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.UploadIntervalS));
        do
        {
            bool full;
            do
            {
                full = await TrySendBatchAsync(ct) == BatchOutcome.SentFull;
            }
            while (full && !ct.IsCancellationRequested);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private enum BatchOutcome { Sent, SentFull, Failed }

    private async Task<BatchOutcome> TrySendBatchAsync(CancellationToken ct)
    {
        var o = options.Value;
        var rows = outbox.PeekBatch(o.MaxBatchRows);
        var batch = new SampleBatch(
            MachineId: o.MachineId,
            InstallId: outbox.InstallId,
            AgentVersion: AgentVersion,
            Samples: rows,
            Heartbeat: new Heartbeat(
                ActiveUser: $"{Environment.UserDomainName}\\{Environment.UserName}",
                OutboxDepth: outbox.Depth(),
                RestartCount: 0,
                LastError: null,
                ConfigVersion: 0));

        try
        {
            using var response = await _http.PostAsync(
                Routes.Samples, JsonContent.Create(batch, options: JsonDefaults.Options), ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Upload of {Count} rows failed: HTTP {Status}", rows.Count, (int)response.StatusCode);
                return BatchOutcome.Failed;
            }

            var ack = await response.Content.ReadFromJsonAsync<AckResponse>(JsonDefaults.Options, ct)
                      ?? throw new InvalidOperationException("empty ack");

            var deleted = outbox.DeleteThrough(ack.AckThrough);
            logger.LogInformation("Sent {Count} rows, ack_through {Ack}, deleted {Deleted}, {Depth} left",
                rows.Count, ack.AckThrough, deleted, outbox.Depth());

            return rows.Count == o.MaxBatchRows ? BatchOutcome.SentFull : BatchOutcome.Sent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Upload of {Count} rows failed: {Reason}", rows.Count, ex.Message);
            return BatchOutcome.Failed;
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
