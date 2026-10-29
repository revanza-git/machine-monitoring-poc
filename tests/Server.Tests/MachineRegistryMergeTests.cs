using System.Text;
using Microsoft.Extensions.Configuration;
using Server.Auth;

namespace Server.Tests;

/// <summary>
/// The registry is the Machines array merged from appsettings.json and the
/// optional machines.json. .NET configuration merges arrays by index, which
/// is why machines.json must repeat the appsettings.json entry at index 0
/// before adding its own: these tests pin that rule down.
/// </summary>
public class MachineRegistryMergeTests
{
    private const string Base = """{ "Machines": [ { "Name": "WS-DEV-01", "ApiKey": "dev-key" } ] }""";

    private static MachineRegistry Merge(string overlay)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Base)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(overlay)))
            .Build();
        return new MachineRegistry(config);
    }

    [Fact]
    public void Overlay_that_repeats_index_zero_keeps_the_base_machine_and_adds_its_own()
    {
        var registry = Merge("""
            { "Machines": [
                { "Name": "WS-DEV-01", "ApiKey": "dev-key" },
                { "Name": "SIM-0001", "ApiKey": "sim-key-0001" },
                { "Name": "SIM-0002", "ApiKey": "sim-key-0002" } ] }
            """);

        Assert.Equal("WS-DEV-01", registry.Resolve("dev-key")?.Name);
        Assert.Equal("SIM-0001", registry.Resolve("sim-key-0001")?.Name);
        Assert.Equal("SIM-0002", registry.Resolve("sim-key-0002")?.Name);
    }

    [Fact]
    public void Overlay_starting_at_index_zero_replaces_the_base_machine()
    {
        // The trap: an overlay that only lists its own machines silently
        // evicts WS-DEV-01, because index 0 of the overlay wins.
        var registry = Merge("""{ "Machines": [ { "Name": "SIM-0001", "ApiKey": "sim-key-0001" } ] }""");

        Assert.Null(registry.Resolve("dev-key"));
        Assert.Equal("SIM-0001", registry.Resolve("sim-key-0001")?.Name);
    }

    [Fact]
    public void Empty_overlay_leaves_the_base_machine_alone()
    {
        var registry = Merge("{}");
        Assert.Equal("WS-DEV-01", registry.Resolve("dev-key")?.Name);
    }
}
