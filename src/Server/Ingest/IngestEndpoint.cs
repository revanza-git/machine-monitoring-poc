using Microsoft.Extensions.Options;
using Server.Auth;
using Server.Config;
using Shared;

namespace Server.Ingest;

/// <summary>
/// POST /v1/samples: the one endpoint agents call. Authenticate, validate,
/// store, acknowledge. received_at is stamped here, on the server clock.
/// The reply carries the current config only when the agent's heartbeat
/// reports an older version, so a steady-state reply is just the ack.
/// </summary>
public static class IngestEndpoint
{
    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
    {
        // Throttle first: shedding load must be the cheapest thing the server does.
        app.MapPost(Routes.Samples, HandleAsync)
            .AddEndpointFilter(app.ServiceProvider.GetRequiredService<IngestThrottle>())
            .AddEndpointFilter<ApiKeyFilter>();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        SampleBatch batch, HttpContext http, ISampleStore store,
        IOptionsMonitor<AgentConfigOptions> config, ILogger<SampleBatch> logger, CancellationToken ct)
    {
        var machine = http.Machine();
        var receivedAt = DateTimeOffset.UtcNow;

        if (BatchValidator.Validate(batch) is { } problem)
        {
            logger.LogWarning("Rejected batch from {Machine}: {Problem}", machine.Name, problem);
            return Results.BadRequest(new { error = problem });
        }

        logger.LogInformation(
            "Batch from {Machine} (reports {Reported}, install {Install}, agent {Version}, config v{Config}{Stopping}): {Count} samples, outbox depth {Depth}, user {User}",
            machine.Name, batch.MachineId, batch.InstallId, batch.AgentVersion, batch.Heartbeat.ConfigVersion,
            batch.Heartbeat.Stopping ? ", stopping" : "", batch.Samples.Count, batch.Heartbeat.OutboxDepth, batch.Heartbeat.ActiveUser);

        var ackThrough = await store.StoreAsync(machine, batch, receivedAt, ct);

        var current = config.CurrentValue;
        var reply = batch.Heartbeat.ConfigVersion == current.Version ? null : current.ToContract();
        if (reply is not null)
            logger.LogInformation("Sending config v{Version} to {Machine} (agent has v{Agent})", current.Version, machine.Name, batch.Heartbeat.ConfigVersion);

        return Results.Ok(new AckResponse(ackThrough, reply));
    }
}
