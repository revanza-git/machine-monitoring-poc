using System.Diagnostics;

namespace Agent.Sampling;

/// <summary>
/// Reads "Processor Information\% Processor Time\_Total" every tick and
/// reports the average when the interval closes, so a 60 s value is a mean
/// over the minute rather than whatever instant the timer happened to fire on.
/// </summary>
public sealed class CpuSampler : IDisposable
{
    private readonly PerformanceCounter _counter =
        new("Processor Information", "% Processor Time", "_Total", readOnly: true);

    private double _sum;
    private int _reads;

    public CpuSampler()
    {
        // The first read of a rate counter is always 0: it only establishes a baseline.
        _counter.NextValue();
    }

    public void Tick()
    {
        _sum += _counter.NextValue();
        _reads++;
    }

    /// <summary>Average since the last close, or null if no tick happened. Resets the window.</summary>
    public double? CloseInterval()
    {
        if (_reads == 0) return null;
        var average = _sum / _reads;
        _sum = 0;
        _reads = 0;
        return Math.Round(average, 1);
    }

    public void Dispose() => _counter.Dispose();
}
