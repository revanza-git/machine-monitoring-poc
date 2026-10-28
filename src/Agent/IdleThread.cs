using System.Runtime.InteropServices;

namespace Agent;

/// <summary>
/// Runs one piece of work on its own thread at the lowest CPU priority and in
/// Windows background processing mode, which also lowers its I/O and memory
/// priority: the design's "idle priority, throttled I/O" for the folder walk.
/// A dedicated thread rather than the pool, so the settings cannot leak into
/// unrelated work that would otherwise land on the same pool thread later;
/// the background mode is ended in a finally for the same reason.
/// </summary>
public static class IdleThread
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var background = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                if (background)
                    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
            }
        })
        {
            Name = name,
            IsBackground = true,            // never keeps the process alive on shutdown
            Priority = ThreadPriority.Lowest,
        };
        thread.Start();
        return done.Task;
    }

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
}
