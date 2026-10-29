using Agent;

namespace Agent.Tests;

/// <summary>
/// The retry maths every outage depends on: doubling with full jitter, capped
/// at the maximum, back to the start after a success.
/// </summary>
public class BackoffTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(5);

    [Fact]
    public void First_delay_is_half_to_all_of_the_initial()
    {
        var backoff = new Backoff(Initial, TimeSpan.FromSeconds(300));
        Assert.InRange(backoff.Next(), Initial / 2, Initial);
        Assert.Equal(1, backoff.Failures);
    }

    [Fact]
    public void Each_failure_doubles_the_base_delay()
    {
        var backoff = new Backoff(Initial, TimeSpan.FromHours(1));
        for (var k = 0; k < 6; k++)
        {
            var expectedBase = Initial * (1 << k);
            Assert.InRange(backoff.Next(), expectedBase / 2, expectedBase);
        }
        Assert.Equal(6, backoff.Failures);
    }

    [Fact]
    public void Delay_never_exceeds_the_maximum()
    {
        var max = TimeSpan.FromSeconds(30);
        var backoff = new Backoff(Initial, max);
        for (var i = 0; i < 20; i++) backoff.Next();
        for (var i = 0; i < 50; i++) Assert.InRange(backoff.Next(), max / 2, max);   // where a real agent sits after a long outage
    }

    [Fact]
    public void Reset_returns_to_the_initial_delay()
    {
        var backoff = new Backoff(Initial, TimeSpan.FromSeconds(300));
        for (var i = 0; i < 5; i++) backoff.Next();
        backoff.Reset();
        Assert.Equal(0, backoff.Failures);
        Assert.InRange(backoff.Next(), Initial / 2, Initial);
    }

    [Fact]
    public void Jitter_stays_between_half_and_the_whole_delay()
    {
        var delay = TimeSpan.FromSeconds(10);
        for (var i = 0; i < 1000; i++)
            Assert.InRange(Backoff.Jitter(delay), delay / 2, delay);
    }
}
