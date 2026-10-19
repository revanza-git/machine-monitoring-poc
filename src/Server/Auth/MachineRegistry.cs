namespace Server.Auth;

/// <summary>The machine the server resolved from the credential. Never from the body.</summary>
public sealed record MachineIdentity(string Name);

/// <summary>
/// PoC stand-in for the certificate thumbprint lookup in the design: each
/// registered machine has one API key, configured under "Machines" in
/// appsettings. Unknown keys are rejected, mirroring "unknown certificates
/// require admin approval".
/// </summary>
public sealed class MachineRegistry
{
    public sealed record Entry(string Name, string ApiKey);

    private readonly Dictionary<string, MachineIdentity> _byKey;

    public MachineRegistry(IConfiguration configuration)
    {
        var entries = configuration.GetSection("Machines").Get<List<Entry>>() ?? [];
        _byKey = entries.ToDictionary(e => e.ApiKey, e => new MachineIdentity(e.Name), StringComparer.Ordinal);
    }

    public MachineIdentity? Resolve(string? apiKey) =>
        apiKey is not null && _byKey.TryGetValue(apiKey, out var m) ? m : null;
}
