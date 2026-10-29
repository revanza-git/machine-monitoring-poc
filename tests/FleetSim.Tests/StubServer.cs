using System.Net;
using System.Net.Http.Json;
using Agent;
using FleetSim;
using Shared;

namespace FleetSim.Tests;

/// <summary>
/// A fake ingest endpoint for the upload-step tests: answers with whatever
/// the test says and records each request's API key and body, so the agent
/// can be driven through 2xx, 429, 5xx and exceptions without a network.
/// </summary>
internal sealed class StubServer(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string? ApiKey, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Headers.TryGetValues(VirtualAgent.ApiKeyHeader, out var v) ? v.First() : null, body));
        return respond(body);
    }

    public static HttpResponseMessage Ack(long ackThrough, AgentConfig? config = null) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new AckResponse(ackThrough, config), options: JsonDefaults.Options) };

    public static HttpResponseMessage TooManyRequests(int retryAfterS)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterS));
        return response;
    }

    /// <summary>An agent wired to this stub, with the real backoff (5 s initial, 300 s max unless told otherwise).</summary>
    public static (VirtualAgent Agent, StubServer Server, Stats Stats) Agent(
        Func<string, HttpResponseMessage> respond, double lostAckPct = 0, int maxRows = SampleBatch.MaxRows, int backoffMaxS = 300)
    {
        var server = new StubServer(respond);
        var http = new HttpClient(server) { BaseAddress = new Uri("https://stub.local") };
        var stats = new Stats();
        var agent = new VirtualAgent(1, http, stats, new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(backoffMaxS)),
            seed: 7, lostAckPct: lostAckPct, maxRows: maxRows);
        return (agent, server, stats);
    }
}
