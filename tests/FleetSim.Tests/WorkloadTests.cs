using FleetSim;

namespace FleetSim.Tests;

public class WorkloadTests
{
    [Fact]
    public void Same_seed_gives_the_same_series()
    {
        var a = new Workload(42);
        var b = new Workload(42);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextCpuMem(), b.NextCpuMem());
            Assert.Equal(a.NextDrives().Drives, b.NextDrives().Drives);
        }
    }

    [Fact]
    public void Values_stay_inside_plausible_bounds()
    {
        var w = new Workload(3);
        for (var i = 0; i < 2000; i++)
        {
            var cpu = w.NextCpuMem();
            Assert.InRange(cpu.CpuPct, 2, 95);
            Assert.Equal(Math.Round(cpu.CpuPct, 1), cpu.CpuPct);          // one decimal, like the real sampler
            Assert.InRange(cpu.MemUsedMb, 1, cpu.MemTotalMb);
            Assert.NotNull(cpu.CommitMb);
            Assert.InRange(cpu.CommitMb!.Value, cpu.MemUsedMb, cpu.CommitLimitMb!.Value);

            var drives = w.NextDrives();
            Assert.Equal(2, drives.Drives.Count);
            foreach (var d in drives.Drives)
                Assert.InRange(d.FreeBytes, 1, d.TotalBytes);
        }
    }
}
