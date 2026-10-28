using System.Runtime.InteropServices;

namespace Agent.Sampling;

/// <summary>
/// Physical memory and commit charge via GlobalMemoryStatusEx, the call named
/// in the design. Microseconds per read. Commit charge is the memory the
/// system has promised to processes (ullTotalPageFile minus ullAvailPageFile,
/// despite the names: that pair is the commit limit and what is left of it),
/// which is what climbs when a leaking process is about to exhaust the box.
/// </summary>
public static class MemorySampler
{
    public readonly record struct Reading(long TotalMb, long AvailableMb, long CommitMb, long CommitLimitMb)
    {
        public long UsedMb => TotalMb - AvailableMb;
    }

    public static Reading Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed: {Marshal.GetLastWin32Error()}");

        const ulong Mb = 1024 * 1024;
        return new Reading(
            TotalMb: (long)(status.ullTotalPhys / Mb),
            AvailableMb: (long)(status.ullAvailPhys / Mb),
            CommitMb: (long)((status.ullTotalPageFile - status.ullAvailPageFile) / Mb),
            CommitLimitMb: (long)(status.ullTotalPageFile / Mb));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
