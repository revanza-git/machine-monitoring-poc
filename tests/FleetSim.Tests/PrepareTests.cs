using System.Text.Json.Nodes;
using FleetSim;

namespace FleetSim.Tests;

public class PrepareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleetsim-{Guid.NewGuid():N}");
    private readonly string _settings;
    private readonly string _out;

    public PrepareTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = Path.Combine(_dir, "appsettings.json");
        _out = Path.Combine(_dir, "config", "machines.json");   // the folder does not exist yet
        File.WriteAllText(_settings, """{ "Logging": {}, "Machines": [ { "Name": "WS-DEV-01", "ApiKey": "dev-key" } ] }""");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonArray Machines() => JsonNode.Parse(File.ReadAllText(_out))!["Machines"]!.AsArray();

    [Fact]
    public void Base_machine_comes_first_then_zero_padded_sim_entries()
    {
        Prepare.Run(new PrepareOptions(3, _out, _settings));

        var machines = Machines();
        Assert.Equal(4, machines.Count);
        Assert.Equal("WS-DEV-01", machines[0]!["Name"]!.GetValue<string>());
        Assert.Equal("dev-key", machines[0]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("SIM-0001", machines[1]!["Name"]!.GetValue<string>());
        Assert.Equal("sim-key-0001", machines[1]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("SIM-0003", machines[3]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void Second_run_replaces_sim_entries_and_keeps_hand_added_ones()
    {
        Prepare.Run(new PrepareOptions(5, _out, _settings));
        var root = JsonNode.Parse(File.ReadAllText(_out))!.AsObject();
        root["Machines"]!.AsArray().Add(new JsonObject { ["Name"] = "LAB-01", ["ApiKey"] = "lab-key" });
        root["Note"] = "kept";
        File.WriteAllText(_out, root.ToJsonString());

        Prepare.Run(new PrepareOptions(2, _out, _settings));

        var names = Machines().Select(m => m!["Name"]!.GetValue<string>()).ToList();
        Assert.Equal(["WS-DEV-01", "LAB-01", "SIM-0001", "SIM-0002"], names);
        Assert.Equal("kept", JsonNode.Parse(File.ReadAllText(_out))!["Note"]!.GetValue<string>());
    }
}
