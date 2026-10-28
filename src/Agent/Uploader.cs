using System.Net;
using System.Net.Http.Json;
using Agent.Sampling;
using Microsoft.Extensions.Options;
using Shared;

namespace Agent;

/// <summary>
/// Drains the outbox to the server. Each cycle peeks the oldest rows (at most
/// <see cref="AgentOptions.MaxBatchRows"/>), posts them with a heartbeat, and
/// deletes rows only after the server's ack_through covers them. An ack that
/// carries config is applied through the <see cref="ConfigCache"/>.
///
/// Pacing: after a success wait the normal interval; after a full batch go
/// again at once so a backlog drains in capped batches; after any failure
/// (timeout, 5xx, 429, bad certificate, DNS) wait an exponentially growing,
/// jittered delay. A 429 honours the server's Retry-After instead. Sampling
/// never stops while this waits: the outbox simply grows, up to its age cap.
///
/// On clean shutdown a final heartbeat with stopping=true is sent, best
/// effort, so the portal can show "off" instead of "unresponsive".
/// </summary>
public sealed class Uploader(IOptions<AgentOptions> options, Outbox outbox, ConfigCache config, LastError lastError, ILogger<Uploader> logger)
    : BackgroundService
{
    private const string Source = "upload";

    private static readonly string AgentVersion =
        typeof(Uploader).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly HttpClient _http = CreateClient(options.Value);
    private readonly Backoff _backoff = new(
        TimeSpan.FromSeconds(options.Value.BackoffInitialS),
        TimeSpan.FromSeconds(options.Value.BackoffMaxS));

    private readonly int _restartCount = outbox.IncrementRestartCount();

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
        logger.LogInformation("Uploading to {Server} every {Interval}s, up to {Max} rows per batch, outbox cap {Days} days, restart #{Restarts}",
            o.ServerUrl, o.UploadIntervalS, o.MaxBatchRows, o.OutboxMaxAgeDays, _restartCount);
        if (o.TrustAnyServerCertificate)
            logger.LogWarning("TrustAnyServerCertificate is on: server certificate is NOT validated (development only)");

        while (!ct.IsCancellationRequested)
        {
            PruneOutbox();
            var outcome = await TrySendBatchAsync(stopping: false, ct);

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

    /// <summary>The goodbye: a heartbeat-only post flagged stopping. Best effort, five seconds at most.</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        logger.LogInformation("Sending final heartbeat (stopping)");
        await TrySendBatchAsync(stopping: true, cts.Token);
    }

    private abstract record Outcome
    {
        public sealed record Sent : Outcome;
        public sealed record SentFull : Outcome;
        public sealed record Throttled(TimeSpan RetryAfter) : Outcome;
        public sealed record Failed : Outcome;
    }

    private async Task<Outcome> TrySendBatchAsync(bool stopping, CancellationToken ct)
    {
        var o = options.Value;
        List<Sample> rows = stopping ? [] : outbox.PeekBatch(o.MaxBatchRows);
        var user = ReadUser();
        var batch = new SampleBatch(
            MachineId: o.MachineId,
            InstallId: outbox.InstallId,
            AgentVersion: AgentVersion,
            Samples: rows,
            Heartbeat: new Heartbeat(
                ActiveUser: user.ActiveUser,
                LastUser: user.LastUser,
                OutboxDepth: outbox.Depth(),
                RestartCount: _restartCount,
                LastError: lastError.Message,
                ConfigVersion: config.Current.Version,
                Stopping: stopping));

        try
        {
            using var response = await _http.PostAsync(
                Routes.Samples, JsonContent.Create(batch, options: JsonDefaults.Options), ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? _backoff.Next();
                return Fail(new Outcome.Throttled(retryAfter), $"429, Retry-After {retryAfter.TotalSeconds:0.#}s");
            }

            if (!response.IsSuccessStatusCode)
                return Fail(new Outcome.Failed(), $"HTTP {(int)response.StatusCode}");

            var ack = await response.Content.ReadFromJsonAsync<AckResponse>(JsonDefaults.Options, ct)
                      ?? throw new InvalidOperationException("empty ack");

            var deleted = outbox.DeleteThrough(ack.AckThrough);
            _backoff.Reset();
            lastError.Clear(Source);
            logger.LogInformation("Sent {Count} rows, ack_through {Ack}, deleted {Deleted}, {Depth} left",
                rows.Count, ack.AckThrough, deleted, outbox.Depth());

            if (ack.Config is { } newConfig)
                config.Apply(newConfig);

            return rows.Count == o.MaxBatchRows ? new Outcome.SentFull() : new Outcome.Sent();
        }
        catch (OperationCanceledException) when (stopping)
        {
            return Fail(new Outcome.Failed(), "goodbye timed out");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Connection refused, timeout, bad certificate, a 200 that is not
            // JSON (a proxy page), a SQLite error under the outbox: whatever it
            // was, the rows are still in the outbox and the backoff decides
            // when to try again. Only shutdown is allowed out of here.
            return Fail(new Outcome.Failed(), ex.GetBaseException().Message);
        }

        Outcome Fail(Outcome outcome, string reason)
        {
            lastError.Record(Source, reason);
            logger.LogWarning("Upload of {Count} rows failed: {Reason}", rows.Count, reason);
            return outcome;
        }
    }

    /// <summary>
    /// Who is at the keyboard, asked fresh for every heartbeat. A WTS or
    /// registry fault must not stop uploads, so it degrades to "unknown".
    /// </summary>
    private UserSampler.Reading ReadUser()
    {
        try
        {
            return UserSampler.Read();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the active user");
            return default;
        }
    }

    /// <summary>The "no data loss" promise holds up to the cap; past it, oldest rows go first and loudly.</summary>
    private void PruneOutbox()
    {
        try
        {
            var dropped = outbox.DeleteOlderThan(TimeSpan.FromDays(options.Value.OutboxMaxAgeDays));
            if (dropped > 0)
                logger.LogError("Outbox cap reached: dropped {Dropped} rows older than {Days} days", dropped, options.Value.OutboxMaxAgeDays);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Outbox prune failed");
            lastError.Record("outbox", ex.GetBaseException().Message);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
