using Agent;

namespace Agent.Tests;

public class IdleThreadTests
{
    [Fact]
    public async Task Work_runs_on_a_named_lowest_priority_thread_and_returns_its_result()
    {
        var seen = await IdleThread.Run(() => (Thread.CurrentThread.Name, Thread.CurrentThread.Priority, Thread.CurrentThread.IsThreadPoolThread), "walk");

        Assert.Equal("walk", seen.Name);
        Assert.Equal(ThreadPriority.Lowest, seen.Priority);
        Assert.False(seen.IsThreadPoolThread);
    }

    [Fact]
    public async Task Exception_in_the_work_surfaces_to_the_awaiter()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => IdleThread.Run<int>(() => throw new IOException("disk gone"), "walk"));
        Assert.Equal("disk gone", ex.Message);
    }

    [Fact]
    public async Task Calling_thread_is_left_alone()
    {
        var before = Thread.CurrentThread.Priority;
        await IdleThread.Run(() => 1, "walk");
        Assert.Equal(before, Thread.CurrentThread.Priority);
    }
}
