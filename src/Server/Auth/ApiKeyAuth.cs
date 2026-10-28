namespace Server.Auth;

/// <summary>
/// Resolves the X-Api-Key header to a registered machine and stores it in
/// HttpContext.Items for the handler. 401 when missing or unknown.
///
/// Middleware on the ingest path rather than an endpoint filter: a filter
/// runs only after the body has been read and parsed, so a request with no
/// valid key would still cost a full JSON parse. Here it costs one
/// dictionary lookup. One path, one header, so it stays easy to read and
/// defend without the authentication framework.
/// </summary>
public sealed class ApiKeyAuth(MachineRegistry registry) : IMiddleware
{
    public const string HeaderName = "X-Api-Key";
    public const string ItemKey = "machine";

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var machine = registry.Resolve(ctx.Request.Headers[HeaderName].FirstOrDefault());
        if (machine is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ctx.Items[ItemKey] = machine;
        await next(ctx);
    }
}

public static class HttpContextMachineExtensions
{
    public static MachineIdentity Machine(this HttpContext ctx) =>
        (MachineIdentity)ctx.Items[ApiKeyAuth.ItemKey]!;
}
