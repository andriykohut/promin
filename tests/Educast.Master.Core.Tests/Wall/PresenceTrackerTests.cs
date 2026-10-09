using Educast.Master.Core.Wall;

namespace Educast.Master.Core.Tests.Wall;

public class PresenceTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    [Fact]
    public void NeverSeen_IsOffline()
    {
        var presence = new PresenceTracker(Timeout);

        Assert.False(presence.IsOnline("device-01", T0));
    }

    [Fact]
    public void SeenJustNow_IsOnline()
    {
        var presence = new PresenceTracker(Timeout);
        presence.Seen("device-01", T0);

        Assert.True(presence.IsOnline("device-01", T0));
    }

    [Fact]
    public void SeenFiveSecondsAgo_IsOnline()
    {
        var presence = new PresenceTracker(Timeout);
        presence.Seen("device-01", T0);

        Assert.True(presence.IsOnline("device-01", T0.AddSeconds(5)));
    }

    [Fact]
    public void SeenSixSecondsAgo_IsOffline()
    {
        var presence = new PresenceTracker(Timeout);
        presence.Seen("device-01", T0);

        Assert.False(presence.IsOnline("device-01", T0.AddSeconds(6)));
    }

    [Fact]
    public void SeenAgain_IsOnlineAgain()
    {
        var presence = new PresenceTracker(Timeout);
        presence.Seen("device-01", T0);
        Assert.False(presence.IsOnline("device-01", T0.AddSeconds(10)));

        presence.Seen("device-01", T0.AddSeconds(10));

        Assert.True(presence.IsOnline("device-01", T0.AddSeconds(10)));
    }

    [Fact]
    public void EachPc_IsTrackedSeparately()
    {
        var presence = new PresenceTracker(Timeout);
        presence.Seen("device-01", T0);

        Assert.True(presence.IsOnline("device-01", T0));
        Assert.False(presence.IsOnline("device-02", T0));
    }
}
