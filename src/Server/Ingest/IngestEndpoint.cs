using Server.Auth;
using Shared;

namespace Server.Ingest;

/// <summary>
/// POST /v1/samples: the one endpoint agents call. Authenticate, validate,
/// store, acknowledge. received_at is stamped here, on the server clock.
/// </summary>
public static class IngestEndpoint
{
    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
    {
        app.MapPost(Routes.Samples, HandleAsync).AddEndpointFilter<ApiKeyFilter>();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        SampleBatch batch, HttpContext http, ISampleStore store, ILogger<SampleBatch> logger, CancellationToken ct)
    {
        var machine = http.Machine();
        var receivedAt = DateTimeOffset.UtcNow;

        if (BatchValidator.Validate(batch) is { } problem)
        {
            logger.LogWarning("Rejected batch from {Machine}: {Problem}", machine.Name, problem);
            return Results.BadRequest(new { error = problem });
        }

        logger.LogInformation(
            "Batch from {Machine} (reports {Reported}, install {Install}, agent {Version}): {Count} samples, outbox depth {Depth}, user {User}",
            machine.Name, batch.MachineId, batch.InstallId, batch.AgentVersion,
            batch.Samples.Count, batch.Heartbeat.OutboxDepth, batch.Heartbeat.ActiveUser);

        var ackThrough = await store.StoreAsync(machine, batch, receivedAt, ct);
        return Results.Ok(new AckResponse(ackThrough, Config: null));
    }
}
