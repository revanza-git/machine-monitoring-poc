using Agent;

namespace Agent.Tests;

public class LastErrorTests
{
    [Fact]
    public void Starts_empty()
    {
        Assert.Null(new LastError().Message);
    }

    [Fact]
    public void Records_source_and_reason_with_a_utc_time()
    {
        var e = new LastError();
        e.Record("upload", "connection refused");
        Assert.Matches(@"^\d\d:\d\d:\d\dZ upload failed: connection refused$", e.Message);
    }

    [Fact]
    public void Latest_fault_wins_whatever_its_source()
    {
        var e = new LastError();
        e.Record("upload", "HTTP 503");
        e.Record("sampling", "counter gone");
        Assert.Contains("sampling failed: counter gone", e.Message);
    }

    [Fact]
    public void Clearing_one_source_leaves_another_sources_fault_alone()
    {
        var e = new LastError();
        e.Record("sampling", "counter gone");
        e.Clear("upload");
        Assert.NotNull(e.Message);
    }

    [Fact]
    public void Clearing_the_same_source_empties_the_slot()
    {
        var e = new LastError();
        e.Record("upload", "HTTP 503");
        e.Clear("upload");
        Assert.Null(e.Message);
    }
}
