using System.Runtime.InteropServices;

namespace Agent.Sampling;

/// <summary>Physical memory via GlobalMemoryStatusEx, the call named in the design. Microseconds per read.</summary>
public static class MemorySampler
{
    public readonly record struct Reading(long TotalMb, long AvailableMb)
    {
        public long UsedMb => TotalMb - AvailableMb;
    }

    public static Reading Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed: {Marshal.GetLastWin32Error()}");

        return new Reading(
            TotalMb: (long)(status.ullTotalPhys / (1024 * 1024)),
            AvailableMb: (long)(status.ullAvailPhys / (1024 * 1024)));
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
