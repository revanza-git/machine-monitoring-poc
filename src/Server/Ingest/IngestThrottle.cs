using Microsoft.Extensions.Options;

namespace Server.Ingest;

public sealed class IngestOptions
{
    public const string Section = "Ingest";

    /// <summary>Batches being processed at once before new ones are told to come back later.</summary>
    public int MaxInFlight { get; set; } = 16;

    /// <summary>Sent as Retry-After on a 429. Agents add jitter on top.</summary>
    public int RetryAfterS { get; set; } = 5;
}

/// <summary>
/// Back-pressure for the ingest endpoint. A semaphore bounds how many batches
/// are inside the database transaction at once; when it is full the request
/// is answered 429 with Retry-After immediately instead of queueing. The
/// agent keeps the rows in its outbox and retries later, so shedding load
/// here costs nothing: the data is already safe on the machine.
///
/// This is the "server answers 429 if ingest is saturated" line of the
/// design, in its simplest form. Production would size the limit from the
/// connection pool and expose the rejection count as a metric.
/// </summary>
public sealed class IngestThrottle(IOptions<IngestOptions> options, ILogger<IngestThrottle> logger) : IEndpointFilter
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaxInFlight, options.Value.MaxInFlight);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (!_slots.Wait(0))
        {
            logger.LogWarning("Ingest saturated ({Max} in flight); answering 429", options.Value.MaxInFlight);
            ctx.HttpContext.Response.Headers.RetryAfter = options.Value.RetryAfterS.ToString();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        try
        {
            return await next(ctx);
        }
        finally
        {
            _slots.Release();
        }
    }
}
