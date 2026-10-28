using System.Text.Json;
using Shared;

namespace Agent.Tests;

/// <summary>The heartbeat as the agent puts it on the wire: snake_case, nulls omitted.</summary>
public class HeartbeatWireTests
{
    private static Heartbeat Beat(string? active, string? last) =>
        new(ActiveUser: active, LastUser: last, OutboxDepth: 0, RestartCount: 1, RestartsLastHour: 1, LastError: null, ConfigVersion: 1);

    [Fact]
    public void Both_user_fields_are_snake_case()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Beat(@"CORP\alice", @"CORP\bob"), JsonDefaults.Options));
        Assert.Equal(@"CORP\alice", doc.RootElement.GetProperty("active_user").GetString());
        Assert.Equal(@"CORP\bob", doc.RootElement.GetProperty("last_user").GetString());
    }

    [Fact]
    public void Nobody_signed_in_omits_the_fields_and_reads_back_as_null()
    {
        var json = JsonSerializer.Serialize(Beat(null, null), JsonDefaults.Options);
        Assert.DoesNotContain("active_user", json);
        Assert.DoesNotContain("last_user", json);

        var back = JsonSerializer.Deserialize<Heartbeat>(json, JsonDefaults.Options)!;
        Assert.Null(back.ActiveUser);
        Assert.Null(back.LastUser);
    }
}
