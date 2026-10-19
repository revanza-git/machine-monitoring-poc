using System.Diagnostics;

namespace Agent;

/// <summary>
/// The "one flag" service install. The agent is the same binary either way:
/// run it from a console and it is a console app; let the Service Control
/// Manager start it and AddWindowsService makes it a service. --install
/// registers it with sc.exe (auto start, recovery: restart twice then stop,
/// as the design's "stop after three failures in a row" asks); --uninstall
/// removes it. Both need an elevated prompt. The design ships an MSI through
/// Group Policy; this is the PoC-sized version of the same idea.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "MachineMonitoringAgent";

    /// <summary>Returns true if the arguments were a service command and the process should exit.</summary>
    public static bool TryHandle(string[] args)
    {
        if (args.Contains("--install"))
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine executable path");
            Run("sc.exe", $"create {ServiceName} binPath= \"{exe}\" start= auto DisplayName= \"Machine Monitoring Agent\"");
            Run("sc.exe", $"description {ServiceName} \"Samples CPU, memory and drives and pushes them to the monitoring server.\"");
            Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000//");
            Run("sc.exe", $"start {ServiceName}");
            return true;
        }

        if (args.Contains("--uninstall"))
        {
            Run("sc.exe", $"stop {ServiceName}");
            Run("sc.exe", $"delete {ServiceName}");
            return true;
        }

        return false;
    }

    private static void Run(string file, string arguments)
    {
        Console.WriteLine($"> {file} {arguments}");
        using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })!;
        p.WaitForExit();
        if (p.ExitCode == 5)
            Console.WriteLine("  access denied: run this from an elevated (Administrator) prompt");
        else if (p.ExitCode != 0)
            Console.WriteLine($"  exit code {p.ExitCode}");
    }
}
