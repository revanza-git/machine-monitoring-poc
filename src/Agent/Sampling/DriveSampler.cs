using Shared;

namespace Agent.Sampling;

/// <summary>
/// Fixed, ready logical drives only: network and removable drives are skipped
/// as the design says. DriveInfo wraps GetDiskFreeSpaceEx.
/// </summary>
public static class DriveSampler
{
    public static DriveData Read()
    {
        var drives = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            drives.Add(new DriveEntry(d.Name, d.TotalSize, d.AvailableFreeSpace));
        }
        return new DriveData(drives);
    }
}
