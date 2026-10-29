using System.Globalization;
using FleetSim;

// FleetSim: a simulated fleet of agents against the real server, so the
// design's scale numbers can be measured instead of estimated. See README.md.

const string Usage = """
    usage:
      fleetsim prepare --agents N [--out machines.json] [--server-settings src/Server/appsettings.json]
      fleetsim steady  --agents N [--interval 60] [--minutes 10] [--server URL] [--out results/...json]
                       [--backoff-max 300] [--lost-ack-pct 0] [--seed 1]
      fleetsim storm   --agents N [--backlog-hours 6] [--no-jitter] [--backoff-max 300] [--timeout-minutes 15]
                       [--server URL] [--out results/...json] [--lost-ack-pct 0] [--seed 1]
    """;

// Numbers in the console and in file names read the same on every machine.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var defaults = new Defaults();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // let the run print its summary so far
    Console.WriteLine("Ctrl+C: stopping the fleet");
    cts.Cancel();
};

try
{
    var parsed = Args.Parse(args);
    switch (parsed.Command)
    {
        case "prepare":
        {
            var o = PrepareOptions.From(parsed, defaults);
            parsed.EnsureAllUsed();
            return Prepare.Run(o);
        }
        case "steady":
        {
            var o = SteadyOptions.From(parsed, defaults);
            parsed.EnsureAllUsed();
            return Finish(await Fleet.SteadyAsync(o, cts.Token), o.Out, failIfNotDrained: false);
        }
        case "storm":
        {
            var o = StormOptions.From(parsed, defaults);
            parsed.EnsureAllUsed();
            return Finish(await Fleet.StormAsync(o, cts.Token), o.Out, failIfNotDrained: true);
        }
        default:
            throw new ArgumentException($"unknown command '{parsed.Command}'");
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}
catch (NotRegisteredException ex)
{
    Console.Error.WriteLine(ex.Message);   // the run was stopped: nothing it measured would mean anything
    return 3;
}

static int Finish(RunSummary summary, string outPath, bool failIfNotDrained)
{
    Console.WriteLine(summary.ToText());
    summary.WriteJson(outPath);
    Console.WriteLine($"written {outPath}");
    return failIfNotDrained && summary.AgentsNotDrained.Count > 0 ? 1 : 0;
}
