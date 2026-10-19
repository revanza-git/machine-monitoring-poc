namespace Agent;

/// <summary>
/// Exponential backoff with full jitter. Each failure doubles the base delay
/// up to <paramref name="max"/>; the actual wait is a random value between
/// half and the whole of that base, so 200 agents that lost the server in the
/// same second do not come back in the same second either.
/// </summary>
public sealed class Backoff(TimeSpan initial, TimeSpan max)
{
    private int _failures;

    public int Failures => _failures;

    public TimeSpan Next()
    {
        var exponent = Math.Min(_failures, 16);   // 2^16 × initial is already past any sane max
        var baseDelay = TimeSpan.FromTicks(Math.Min(max.Ticks, initial.Ticks * (1L << exponent)));
        _failures++;
        return Jitter(baseDelay);
    }

    public void Reset() => _failures = 0;

    /// <summary>A random value between 50% and 100% of <paramref name="delay"/>.</summary>
    public static TimeSpan Jitter(TimeSpan delay) =>
        TimeSpan.FromTicks((long)(delay.Ticks * (0.5 + Random.Shared.NextDouble() * 0.5)));
}
