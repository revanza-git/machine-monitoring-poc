using System.Diagnostics;
using Agent.Sampling;

namespace Agent.Tests;

/// <summary>Against the real WTS and registry calls: marshalling and permissions, not logic.</summary>
public class UserSamplerWindowsTests
{
    private static int MySessionId => Process.GetCurrentProcess().SessionId;

    [Fact]
    public void Enumerates_the_session_this_test_runs_in()
    {
        var mine = Assert.Single(UserSampler.EnumerateSessions(), s => s.Id == MySessionId);
        Assert.NotEmpty(mine.Station);
    }

    [Fact]
    public void Active_session_user_is_the_current_user()
    {
        var mine = UserSampler.EnumerateSessions().Single(s => s.Id == MySessionId);
        if (!mine.Active) return;   // a service account or build agent: nothing to compare against

        Assert.Equal(Environment.UserName, mine.User, ignoreCase: true);
        Assert.Equal(Environment.UserDomainName, mine.Domain, ignoreCase: true);
    }

    [Fact]
    public void Read_does_not_throw_and_never_reports_a_local_account_as_dot()
    {
        var reading = UserSampler.Read();
        foreach (var name in new[] { reading.ActiveUser, reading.LastUser })
            if (name is not null) Assert.False(name.StartsWith(@".\"), name);
    }
}
