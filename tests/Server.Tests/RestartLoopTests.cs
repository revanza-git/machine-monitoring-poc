using Server.Api;

namespace Server.Tests;

/// <summary>The restart-loop flag: three starts inside an hour, by default.</summary>
public class RestartLoopTests
{
    private static readonly StatusOptions Defaults = new();

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    public void Flags_at_the_threshold_and_above(int? recentStarts, bool expected)
    {
        Assert.Equal(expected, MachineStatus.IsRestartLoop(recentStarts, Defaults));
    }

    [Fact]
    public void Threshold_is_configurable()
    {
        var strict = new StatusOptions { RestartLoopStarts = 2 };
        Assert.True(MachineStatus.IsRestartLoop(2, strict));
        Assert.False(MachineStatus.IsRestartLoop(2, Defaults));
    }
}
