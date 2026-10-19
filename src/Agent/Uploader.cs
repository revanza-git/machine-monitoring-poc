using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// Drains the outbox to the server. Each cycle peeks the oldest rows (at most
/// <see cref="AgentOptions.MaxBatchRows"/>), posts them with a heartbeat, and
/// deletes rows only after the server's ack_through covers them.
///
/// Pacing: after a success wait the normal interval; after a full batch go
/// again at once so a backlog drains in capped batches; after any failure
/// (timeout, 5xx, 429, bad certificate, DNS) wait an exponentially growing,
/// jittered delay. A 429 honours the server's Retry-After instead. Sampling
/// never stops while this waits: the outbox simply grows, up to its age cap.
/// </summary>
public sealed class Uploader(IOptions<AgentOptions> options, Outbox outbox, ILogger<Uploader> logger)
    : BackgroundService
{
    private static readonly string AgentVersion =
        typeof(Uploader).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly HttpClient _http = CreateClient(options.Value);
    private readonly Backoff _backoff = new(
        TimeSpan.FromSeconds(options.Value.BackoffInitialS),
        TimeSpan.FromSeconds(options.Value.BackoffMaxS));

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
        logger.LogInformation("Uploading to {Server} every {Interval}s, up to {Max} rows per batch, outbox cap {Days} days",
            o.ServerUrl, o.UploadIntervalS, o.MaxBatchRows, o.OutboxMaxAgeDays);
        if (o.TrustAnyServerCertificate)
            logger.LogWarning("TrustAnyServerCertificate is on: server certificate is NOT validated (development only)");

        while (!ct.IsCancellationRequested)
        {
            PruneOutbox();
            var outcome = await TrySendBatchAsync(ct);

            var wait = outcome switch
            {
                Outcome.SentFull => TimeSpan.Zero,
                Outcome.Sent => TimeSpan.FromSeconds(o.UploadIntervalS),
                Outcome.Throttled(var retryAfter) => retryAfter + Backoff.Jitter(retryAfter / 2),
                _ => _backoff.Next(),
            };

            if (outcome is Outcome.Failed or Outcome.Throttled)
                logger.LogWarning("Retrying in {Wait:0.#}s (failure #{N}, outbox depth {Depth})",
                    wait.TotalSeconds, _backoff.Failures, outbox.Depth());

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);   // OperationCanceledException on shutdown is the normal exit
        }
    }

    private abstract record Outcome
    {
        public sealed record Sent : Outcome;
        public sealed record SentFull : Outcome;
        public sealed record Throttled(TimeSpan RetryAfter) : Outcome;
        public sealed record Failed : Outcome;
    }

    private async Task<Outcome> TrySendBatchAsync(CancellationToken ct)
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

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? _backoff.Next();
                logger.LogWarning("Server is saturated (429), Retry-After {Seconds:0.#}s", retryAfter.TotalSeconds);
                return new Outcome.Throttled(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Upload of {Count} rows failed: HTTP {Status}", rows.Count, (int)response.StatusCode);
                return new Outcome.Failed();
            }

            var ack = await response.Content.ReadFromJsonAsync<AckResponse>(JsonDefaults.Options, ct)
                      ?? throw new InvalidOperationException("empty ack");

            var deleted = outbox.DeleteThrough(ack.AckThrough);
            _backoff.Reset();
            logger.LogInformation("Sent {Count} rows, ack_through {Ack}, deleted {Deleted}, {Depth} left",
                rows.Count, ack.AckThrough, deleted, outbox.Depth());

            return rows.Count == o.MaxBatchRows ? new Outcome.SentFull() : new Outcome.Sent();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Upload of {Count} rows failed: {Reason}", rows.Count, ex.GetBaseException().Message);
            return new Outcome.Failed();
        }
    }

    /// <summary>The "no data loss" promise holds up to the cap; past it, oldest rows go first and loudly.</summary>
    private void PruneOutbox()
    {
        var dropped = outbox.DeleteOlderThan(TimeSpan.FromDays(options.Value.OutboxMaxAgeDays));
        if (dropped > 0)
            logger.LogError("Outbox cap reached: dropped {Dropped} rows older than {Days} days", dropped, options.Value.OutboxMaxAgeDays);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
