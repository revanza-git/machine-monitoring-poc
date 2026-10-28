using Agent.Sampling;

namespace Agent.Tests;

/// <summary>Against the real GlobalMemoryStatusEx call: the figures must at least be self-consistent.</summary>
public class MemorySamplerTests
{
    [Fact]
    public void Physical_memory_figures_are_consistent()
    {
        var m = MemorySampler.Read();
        Assert.True(m.TotalMb > 0);
        Assert.InRange(m.AvailableMb, 0, m.TotalMb);
        Assert.Equal(m.TotalMb - m.AvailableMb, m.UsedMb);
    }

    [Fact]
    public void Commit_charge_is_positive_and_within_its_limit()
    {
        var m = MemorySampler.Read();
        Assert.True(m.CommitMb > 0);                 // the running system has committed something
        Assert.InRange(m.CommitMb, 0, m.CommitLimitMb);
        Assert.True(m.CommitLimitMb >= m.TotalMb);   // the limit is physical memory plus page file
    }
}
