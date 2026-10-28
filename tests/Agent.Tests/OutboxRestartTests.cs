using Agent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Tests;

/// <summary>
/// The restart-loop signal: starts stamped in the outbox, counted over a
/// sliding hour. Each test gets its own SQLite file in a temp folder.
/// </summary>
public class OutboxRestartTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-outbox-" + Guid.NewGuid().ToString("N"));
    private readonly Outbox _outbox;

    public OutboxRestartTests()
    {
        var options = Options.Create(new AgentOptions { DataDir = _dir });
        _outbox = new Outbox(options, new TempEnvironment(_dir));
    }

    [Fact]
    public void Three_quick_starts_count_as_three_and_the_lifetime_count_matches()
    {
        Assert.Equal(1, _outbox.RecordStart(T0));
        Assert.Equal(2, _outbox.RecordStart(T0.AddSeconds(20)));
        Assert.Equal(3, _outbox.RecordStart(T0.AddSeconds(40)));

        Assert.Equal(3, _outbox.RecentStarts(T0.AddSeconds(40)));
    }

    [Fact]
    public void Starts_older_than_the_window_drop_out_but_the_lifetime_count_keeps_them()
    {
        _outbox.RecordStart(T0);
        _outbox.RecordStart(T0.AddMinutes(1));
        var lifetime = _outbox.RecordStart(T0.AddHours(2));

        Assert.Equal(3, lifetime);
        Assert.Equal(1, _outbox.RecentStarts(T0.AddHours(2)));
    }

    [Fact]
    public void A_steady_hour_brings_the_count_to_zero_without_a_new_start()
    {
        _outbox.RecordStart(T0);
        Assert.Equal(1, _outbox.RecentStarts(T0.AddMinutes(59)));
        Assert.Equal(0, _outbox.RecentStarts(T0.AddMinutes(61)));
    }

    public void Dispose()
    {
        _outbox.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* WAL handle may linger; temp folder */ }
    }

    private sealed class TempEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Agent.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
