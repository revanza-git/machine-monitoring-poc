using FleetSim;

namespace FleetSim.Tests;

public class ArgsTests
{
    private static readonly Defaults D = new();

    [Fact]
    public void Command_then_flags_with_values_and_bare_switches()
    {
        var a = Args.Parse(["storm", "--agents", "40", "--no-jitter", "--backoff-max", "10"]);
        Assert.Equal("storm", a.Command);
        Assert.Equal(40, a.RequireInt("--agents"));
        Assert.True(a.Has("--no-jitter"));
        Assert.Equal(10, a.GetDouble("--backoff-max", 300));
        Assert.Equal(6, a.GetDouble("--backlog-hours", 6));   // fallback when absent
        a.EnsureAllUsed();
    }

    [Fact]
    public void Unknown_flags_are_errors_not_no_ops()
    {
        var a = Args.Parse(["steady", "--agents", "5", "--intervall", "5"]);
        a.RequireInt("--agents");
        var ex = Assert.Throws<ArgumentException>(a.EnsureAllUsed);
        Assert.Contains("--intervall", ex.Message);
    }

    [Theory]
    [InlineData("")]                                   // no command
    [InlineData("--agents 5")]                         // flag where the command should be
    [InlineData("steady 5")]                           // bare value
    [InlineData("steady --agents 5 --agents 6")]       // repeated flag
    public void Malformed_lines_are_rejected(string line) =>
        Assert.Throws<ArgumentException>(() => Args.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Numbers_must_parse()
    {
        Assert.Throws<ArgumentException>(() => Args.Parse(["steady", "--agents", "many"]).RequireInt("--agents"));
        Assert.Throws<ArgumentException>(() => Args.Parse(["steady", "--interval", "fast"]).GetDouble("--interval", 60));
    }

    [Fact]
    public void Steady_options_apply_the_design_defaults()
    {
        var o = SteadyOptions.From(Args.Parse(["steady", "--agents", "200"]), D);
        Assert.Equal(200, o.Agents);
        Assert.Equal(TimeSpan.FromSeconds(60), o.Interval);
        Assert.Equal(10, o.Minutes);
        Assert.Equal(TimeSpan.FromSeconds(300), o.BackoffMax);
        Assert.Equal(0, o.LostAckPct);
        Assert.Equal(1, o.Seed);
        Assert.StartsWith("steady-200-", Path.GetFileName(o.Out));
        Assert.EndsWith(".json", o.Out);
    }

    [Fact]
    public void Storm_options_read_every_flag()
    {
        var o = StormOptions.From(Args.Parse([
            "storm", "--agents", "40", "--backlog-hours", "1", "--no-jitter", "--backoff-max", "10",
            "--timeout-minutes", "3", "--server", "https://server:7181", "--out", "x.json", "--lost-ack-pct", "20", "--seed", "9"]), D);
        Assert.Equal(40, o.Agents);
        Assert.Equal(TimeSpan.FromHours(1), o.Backlog);
        Assert.False(o.Jitter);
        Assert.Equal(TimeSpan.FromSeconds(10), o.BackoffMax);
        Assert.Equal(TimeSpan.FromMinutes(3), o.Timeout);
        Assert.Equal("https://server:7181", o.Server);
        Assert.Equal("x.json", o.Out);
        Assert.Equal(20, o.LostAckPct);
        Assert.Equal(9, o.Seed);
    }

    [Fact]
    public void Agents_is_required_and_positive()
    {
        Assert.Throws<ArgumentException>(() => SteadyOptions.From(Args.Parse(["steady"]), D));
        Assert.Throws<ArgumentException>(() => StormOptions.From(Args.Parse(["storm", "--agents", "0"]), D));
        Assert.Throws<ArgumentException>(() => SteadyOptions.From(Args.Parse(["steady", "--agents", "1", "--lost-ack-pct", "101"]), D));
    }

    [Fact]
    public void Defaults_find_the_repository_root_from_the_test_binary()
    {
        Assert.NotNull(D.RepoRoot);
        Assert.True(File.Exists(Path.Combine(D.RepoRoot!, "MachineMonitoring.slnx")));
        Assert.EndsWith(Path.Combine("src", "Server", "appsettings.json"), D.ServerSettings);
    }
}
