using Shared;

namespace FleetSim;

/// <summary>
/// Plausible values for one virtual agent: slow random walks seeded per
/// agent, so two runs with the same seed send identical payloads and their
/// results are comparable. Nothing here reads the machine the simulator runs
/// on; that is the whole difference from the real agent. Commit charge is
/// included because agent 0.2.0 sends it, so the rows are the real size.
/// </summary>
public sealed class Workload(int seed)
{
    private const long MemTotalMb = 65536;
    private const long CommitLimitMb = 98304;        // RAM plus a 32 GB page file
    private const long TotalC = 1L << 40;             // 1 TB
    private const long TotalD = 2L << 40;             // 2 TB

    private readonly Random _rng = new(seed);
    private double _cpu = 10 + seed % 50;
    private long _memUsedMb = 26000 + seed % 4000;
    private long _freeC = 300L << 30;
    private long _freeD = 1200L << 30;

    public CpuMemData NextCpuMem()
    {
        _cpu = Math.Clamp(_cpu + (_rng.NextDouble() - 0.5) * 10, 2, 95);
        _memUsedMb = Math.Clamp(_memUsedMb + _rng.Next(-250, 251), 8000, 60000);
        return new CpuMemData(Math.Round(_cpu, 1), _memUsedMb, MemTotalMb, _memUsedMb + 9000, CommitLimitMb);
    }

    public DriveData NextDrives()
    {
        _freeC = Drift(_freeC, TotalC);
        _freeD = Drift(_freeD, TotalD);
        return new DriveData([new DriveEntry("C:\\", TotalC, _freeC), new DriveEntry("D:\\", TotalD, _freeD)]);
    }

    /// <summary>Free space mostly shrinks, up to 64 MB a sample, and now and then jumps back up (a clean-up).</summary>
    private long Drift(long free, long total)
    {
        if (_rng.Next(200) == 0) return Math.Min(total, free + (20L << 30));
        return Math.Clamp(free - _rng.Next(0, 64 << 20), 10L << 30, total);
    }
}
