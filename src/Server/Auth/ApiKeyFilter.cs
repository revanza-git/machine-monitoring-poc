namespace Server.Auth;

/// <summary>
/// Endpoint filter: resolves the X-Api-Key header to a registered machine and
/// stores it in HttpContext.Items for the handler. 401 when missing or unknown.
/// A filter rather than the authentication middleware because one endpoint
/// with one header is easier to read and defend.
/// </summary>
public sealed class ApiKeyFilter(MachineRegistry registry) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";
    public const string ItemKey = "machine";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var key = ctx.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
        var machine = registry.Resolve(key);
        if (machine is null)
            return Results.Unauthorized();

        ctx.HttpContext.Items[ItemKey] = machine;
        return await next(ctx);
    }
}

public static class HttpContextMachineExtensions
{
    public static MachineIdentity Machine(this HttpContext ctx) =>
        (MachineIdentity)ctx.Items[ApiKeyFilter.ItemKey]!;
}
