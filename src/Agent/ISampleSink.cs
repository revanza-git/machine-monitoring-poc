using System.Text.Json;
using Shared;

namespace Agent;

/// <summary>
/// Where the sampling loop hands finished samples. The console sink is for
/// seeing numbers; the outbox (next step) is the real destination.
/// </summary>
public interface ISampleSink
{
    ValueTask WriteAsync(string kind, DateTimeOffset sampledAt, object data, CancellationToken ct);
}

public sealed class ConsoleSink(ILogger<ConsoleSink> logger) : ISampleSink
{
    public ValueTask WriteAsync(string kind, DateTimeOffset sampledAt, object data, CancellationToken ct)
    {
        // Same serializer as the wire, so what is printed is what will be sent.
        logger.LogInformation("{Kind} @ {SampledAt:HH:mm:ss}Z {Data}",
            kind, sampledAt, JsonSerializer.Serialize(data, JsonDefaults.Options));
        return ValueTask.CompletedTask;
    }
}
