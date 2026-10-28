using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Agent.Sampling;

/// <summary>
/// Who is at the machine. Under the Service Control Manager the agent runs as
/// LocalSystem, so the process identity says nothing about the user. The
/// design names two sources: WTSEnumerateSessions for the active console
/// user, and the registry (LogonUI\LastLoggedOnUser) for the last user to
/// sign in, which still reads when nobody is signed in right now.
/// </summary>
public static class UserSampler
{
    public readonly record struct Reading(string? ActiveUser, string? LastUser);

    /// <summary>One terminal-services session: "Console", "RDP-Tcp#3", "Services".</summary>
    public sealed record Session(int Id, string Station, bool Active, string? Domain, string? User);

    public static Reading Read() => new(
        ActiveUser: PickActive(EnumerateSessions()),
        LastUser: NormalizeLastLoggedOn(ReadLastLoggedOnRaw(), Environment.MachineName));

    /// <summary>
    /// The user of the active console session, else of any other active
    /// session (RDP). Null at the logon screen or when nobody is signed in;
    /// the portal falls back to the last user then. A locked screen still
    /// counts as active, which is what "at the machine" means for a workstation.
    /// </summary>
    public static string? PickActive(IEnumerable<Session> sessions)
    {
        var signedIn = sessions.Where(s => s.Active && !string.IsNullOrEmpty(s.User)).ToList();
        var pick = signedIn.FirstOrDefault(s => s.Station.Equals("Console", StringComparison.OrdinalIgnoreCase))
                   ?? signedIn.FirstOrDefault();
        return pick is null ? null : Qualify(pick.Domain, pick.User!);
    }

    /// <summary>
    /// LastLoggedOnUser holds "DOMAIN\user", ".\user" for a local account or
    /// "AzureAD\user". The local form is rewritten with the machine name so
    /// both heartbeat fields read the same way.
    /// </summary>
    public static string? NormalizeLastLoggedOn(string? raw, string machineName)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.StartsWith(@".\", StringComparison.Ordinal) ? Qualify(machineName, value[2..]) : value;
    }

    private static string Qualify(string? domain, string user) =>
        string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";

    public static string? ReadLastLoggedOnRaw()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI");
        return key?.GetValue("LastLoggedOnUser") as string;
    }

    /// <summary>All sessions on this machine, with the user of each when one is signed in.</summary>
    public static List<Session> EnumerateSessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new InvalidOperationException($"WTSEnumerateSessions failed: {Marshal.GetLastWin32Error()}");

        try
        {
            var sessions = new List<Session>(count);
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                sessions.Add(new Session(
                    Id: info.SessionId,
                    Station: info.pWinStationName ?? "",
                    Active: info.State == WTS_CONNECTSTATE_CLASS.WTSActive,
                    Domain: Query(info.SessionId, WTS_INFO_CLASS.WTSDomainName),
                    User: Query(info.SessionId, WTS_INFO_CLASS.WTSUserName)));
            }
            return sessions;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? Query(int sessionId, WTS_INFO_CLASS what)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, what, out var buffer, out _))
            return null;   // no right to ask about that session: treat it as nobody

        try
        {
            var value = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected,
        WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit,
    }

    private enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7 }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
