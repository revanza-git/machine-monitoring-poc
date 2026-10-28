using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Server.Ingest;

namespace Server.Tests;

public class IngestThrottleTests
{
    private static IngestThrottle Throttle(int maxInFlight) => new(
        Options.Create(new IngestOptions { MaxInFlight = maxInFlight, RetryAfterS = 7 }),
        NullLogger<IngestThrottle>.Instance);

    [Fact]
    public async Task Beyond_the_limit_answers_429_with_retry_after_and_never_calls_next()
    {
        var throttle = Throttle(1);
        var release = new TaskCompletionSource();
        var first = throttle.InvokeAsync(new DefaultHttpContext(), _ => release.Task);   // holds the only slot

        var second = new DefaultHttpContext();
        var nextCalled = false;
        await throttle.InvokeAsync(second, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("7", second.Response.Headers.RetryAfter.ToString());

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task Slot_is_returned_after_the_request_even_when_it_throws()
    {
        var throttle = Throttle(1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => throttle.InvokeAsync(new DefaultHttpContext(), _ => throw new InvalidOperationException("db down")));

        var ctx = new DefaultHttpContext();
        var nextCalled = false;
        await throttle.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }
}
