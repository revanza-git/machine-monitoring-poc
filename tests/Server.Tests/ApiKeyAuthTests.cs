using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Server.Auth;

namespace Server.Tests;

public class ApiKeyAuthTests
{
    private static ApiKeyAuth Auth() => new(new MachineRegistry(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Machines:0:Name"] = "WS-DEV-01",
            ["Machines:0:ApiKey"] = "dev-key",
        })
        .Build()));

    private static async Task<(DefaultHttpContext Ctx, bool NextCalled)> Send(string? key)
    {
        var ctx = new DefaultHttpContext();
        if (key is not null) ctx.Request.Headers[ApiKeyAuth.HeaderName] = key;
        var nextCalled = false;
        await Auth().InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });
        return (ctx, nextCalled);
    }

    [Fact]
    public async Task Missing_key_is_401_and_the_handler_never_runs()
    {
        var (ctx, nextCalled) = await Send(null);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Unknown_key_is_401()
    {
        var (ctx, nextCalled) = await Send("not-a-key");
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Known_key_resolves_the_machine_for_the_handler()
    {
        var (ctx, nextCalled) = await Send("dev-key");
        Assert.True(nextCalled);
        Assert.Equal("WS-DEV-01", ctx.Machine().Name);
    }
}
