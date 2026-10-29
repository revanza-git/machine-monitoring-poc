using System.Text.Json;
using System.Text.Json.Nodes;

namespace FleetSim;

/// <summary>
/// Writes machines.json, the registry file the server merges over
/// appsettings.json. Configuration merges arrays by index, so the file
/// starts with every entry from appsettings.json (index 0 is WS-DEV-01) and
/// then lists SIM-0001..N. Earlier SIM entries are replaced; entries someone
/// added by hand are kept. The server reads the registry once at startup,
/// so it has to be restarted afterwards.
/// </summary>
public static class Prepare
{
    public static int Run(PrepareOptions o)
    {
        var baseMachines = JsonNode.Parse(File.ReadAllText(o.ServerSettings))?["Machines"]?.AsArray()
            ?? throw new InvalidOperationException($"{o.ServerSettings} has no Machines array");

        var root = File.Exists(o.Out) ? JsonNode.Parse(File.ReadAllText(o.Out)) as JsonObject ?? new JsonObject() : new JsonObject();
        var existing = root["Machines"]?.AsArray() ?? [];

        var machines = new JsonArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in baseMachines.Concat(existing))
        {
            var name = m?["Name"]?.GetValue<string>();
            if (name is null || name.StartsWith("SIM-", StringComparison.Ordinal) || !names.Add(name)) continue;
            machines.Add(m!.DeepClone());
        }
        var kept = machines.Count;

        for (var i = 1; i <= o.Agents; i++)
            machines.Add(new JsonObject { ["Name"] = $"SIM-{i:D4}", ["ApiKey"] = $"sim-key-{i:D4}" });

        root["Machines"] = machines;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
        File.WriteAllText(o.Out, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"{o.Out}: {machines.Count} machines ({kept} kept from {Path.GetFileName(o.ServerSettings)} and earlier entries, {o.Agents} simulated)");
        Console.WriteLine("The server reads the registry at startup: restart it (docker compose restart server)");
        return 0;
    }
}
