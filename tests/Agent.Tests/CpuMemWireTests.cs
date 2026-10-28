using System.Text.Json;
using Shared;

namespace Agent.Tests;

/// <summary>Commit charge on the wire, and rows queued before it existed still parsing.</summary>
public class CpuMemWireTests
{
    [Fact]
    public void Commit_fields_are_snake_case()
    {
        var json = JsonSerializer.Serialize(new CpuMemData(12.5, 8000, 16000, 9000, 24000), JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(9000, doc.RootElement.GetProperty("commit_mb").GetInt64());
        Assert.Equal(24000, doc.RootElement.GetProperty("commit_limit_mb").GetInt64());
    }

    [Fact]
    public void Payload_from_an_older_agent_parses_with_commit_unknown()
    {
        var old = JsonSerializer.Deserialize<CpuMemData>("""{"cpu_pct":12.5,"mem_used_mb":8000,"mem_total_mb":16000}""", JsonDefaults.Options)!;
        Assert.Equal(8000, old.MemUsedMb);
        Assert.Null(old.CommitMb);
        Assert.Null(old.CommitLimitMb);
    }
}
