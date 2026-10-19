using System.Runtime.Versioning;

// The agent reads Windows performance counters and Win32 memory APIs.
// It is Windows-only by design, so the platform analyzer can assume it.
[assembly: SupportedOSPlatform("windows")]
