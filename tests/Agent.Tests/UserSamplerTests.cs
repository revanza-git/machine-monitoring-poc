using static Agent.Sampling.UserSampler;

namespace Agent.Tests;

/// <summary>The session-picking and name rules, fed hand-made session lists.</summary>
public class UserSamplerTests
{
    private static Session S(int id, string station, bool active, string? domain, string? user) =>
        new(id, station, active, domain, user);

    [Fact]
    public void Nobody_signed_in_gives_null()
    {
        var sessions = new[] { S(0, "Services", false, null, null), S(1, "Console", false, null, null) };
        Assert.Null(PickActive(sessions));
    }

    [Fact]
    public void Logon_screen_is_active_but_has_no_user()
    {
        var sessions = new[] { S(0, "Services", false, null, null), S(1, "Console", true, null, null) };
        Assert.Null(PickActive(sessions));
    }

    [Fact]
    public void Active_console_user_is_reported_as_domain_user()
    {
        var sessions = new[] { S(0, "Services", false, null, null), S(2, "Console", true, "CORP", "alice") };
        Assert.Equal(@"CORP\alice", PickActive(sessions));
    }

    [Fact]
    public void Console_wins_over_an_active_remote_session()
    {
        var sessions = new[] { S(3, "RDP-Tcp#1", true, "CORP", "bob"), S(2, "Console", true, "CORP", "alice") };
        Assert.Equal(@"CORP\alice", PickActive(sessions));
    }

    [Fact]
    public void Remote_session_counts_when_the_console_is_empty()
    {
        var sessions = new[] { S(1, "Console", false, null, null), S(3, "RDP-Tcp#1", true, "CORP", "bob") };
        Assert.Equal(@"CORP\bob", PickActive(sessions));
    }

    [Fact]
    public void Disconnected_session_is_not_the_active_user()
    {
        Assert.Null(PickActive(new[] { S(2, "Console", false, "CORP", "alice") }));
    }

    [Fact]
    public void Missing_domain_leaves_a_bare_user()
    {
        Assert.Equal("alice", PickActive(new[] { S(2, "Console", true, null, "alice") }));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(@"CORP\alice", @"CORP\alice")]
    [InlineData(@".\alice", @"WS-DEV-01\alice")]
    [InlineData(@"AzureAD\alice", @"AzureAD\alice")]
    [InlineData(" CORP\\alice \r\n", @"CORP\alice")]
    public void Last_logged_on_value_is_normalised(string? raw, string? expected)
    {
        Assert.Equal(expected, NormalizeLastLoggedOn(raw, "WS-DEV-01"));
    }
}
