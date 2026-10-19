using System.Diagnostics;
using Shared;

namespace Agent.Sampling;

/// <summary>
/// Plain walk of one configured root: each first-level folder is a project
/// and its size is the sum of file lengths beneath it. The directory
/// enumeration returns sizes with the listing (FindFirstFileEx basic info),
/// so there is no per-file stat. Runs on a lowest-priority thread.
///
/// PoC simplifications versus the design: logical size rather than
/// allocated size on disk, and a full walk on a timer rather than a nightly
/// walk kept current by the NTFS change journal.
/// </summary>
public static class ProjectSampler
{
    public static ProjectData Walk(string root)
    {
        var clock = Stopwatch.StartNew();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,   // never follow junctions or symlinks; do count hidden (.git)
        };

        var folders = new List<ProjectEntry>();
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            long bytes = 0;
            int files = 0;
            foreach (var file in dir.EnumerateFiles("*", options))
            {
                bytes += file.Length;
                files++;
            }
            folders.Add(new ProjectEntry(dir.Name, bytes, files));
        }

        return new ProjectData(root, folders, DateTimeOffset.UtcNow, clock.ElapsedMilliseconds);
    }
}
