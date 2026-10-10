using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;

namespace Promin.Master.Core.Tests.Setup;

public sealed class AnnouncementFilterTests
{
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly AnnouncementFilter _filter;

    public AnnouncementFilterTests()
    {
        _filter = new AnnouncementFilter(_pinned.Contains);
    }

    private static AgentPc Pc(int n, string address = "192.168.1.21", int port = 17843, string name = "PC") =>
        new($"device-{n:0000}", name, address, port);

    /// <summary>Two hundred devices nobody paired, each announcing once at <paramref name="at"/>.</summary>
    private void Forge200(long at)
    {
        for (var n = 1; n <= AnnouncementFilter.MaxDevices; n++)
        {
            Assert.True(_filter.ShouldPost(Pc(n), at));
        }
    }

    [Fact]
    public void TheSameAnnouncement_IsPostedEveryHalfSecond()
    {
        Assert.True(_filter.ShouldPost(Pc(1), 0));
        Assert.False(_filter.ShouldPost(Pc(1), 1));
        Assert.False(_filter.ShouldPost(Pc(1), 499));
        Assert.True(_filter.ShouldPost(Pc(1), 500));
        Assert.False(_filter.ShouldPost(Pc(1), 999));
    }

    [Fact]
    public void AnIdThatAlternatesTwoValues_IsPostedAtMostTwiceASecond()
    {
        var posted = 0;
        for (var ms = 0; ms < 1000; ms++)
        {
            if (_filter.ShouldPost(Pc(1, port: ms % 2 == 0 ? 17843 : 17900), ms))
            {
                posted++;
            }
        }

        Assert.Equal(2, posted);
    }

    [Fact]
    public void ARealPc_AnnouncingEveryTwoSeconds_IsNeverDropped_AlsoWhenItsAddressChanges()
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.True(_filter.ShouldPost(Pc(1, address: i < 5 ? "192.168.1.21" : "192.168.1.99"), i * 2000));
        }
    }

    [Fact]
    public void EachPc_IsCountedByItself()
    {
        Assert.True(_filter.ShouldPost(Pc(1), 0));
        Assert.True(_filter.ShouldPost(Pc(2), 0));
        Assert.False(_filter.ShouldPost(Pc(1), 499));
    }

    [Fact]
    public void TwoHundredForgedIds_DoNotHideAPcThisConsoleHasPaired()
    {
        _pinned.Add(Pc(9001).DeviceId);
        Forge200(at: 0);

        Assert.False(_filter.ShouldPost(Pc(201), 1));
        Assert.True(_filter.ShouldPost(Pc(9001), 1));
        Assert.False(_filter.ShouldPost(Pc(9001), 2));
        Assert.True(_filter.ShouldPost(Pc(9001), 501));
    }

    [Fact]
    public void AFullTable_ForgetsTheDevicesNotHeardFromForTenSeconds_SoTheLockOutEndsWithTheFlood()
    {
        Forge200(at: 0);

        Assert.False(_filter.ShouldPost(Pc(201), 9_000));
        Assert.True(_filter.ShouldPost(Pc(201), 10_000));
    }

    /// <summary>A flood of new ids against a full table must not buy a pass over all of it per datagram.</summary>
    [Fact]
    public void AFullTable_IsLookedThroughForTheQuiet_AtMostOnceASecond()
    {
        Forge200(at: 0);

        Assert.False(_filter.ShouldPost(Pc(201), 9_500));
        Assert.False(_filter.ShouldPost(Pc(201), 10_000));
        Assert.False(_filter.ShouldPost(Pc(202), 10_499));
        Assert.True(_filter.ShouldPost(Pc(201), 10_500));
        Assert.True(_filter.ShouldPost(Pc(202), 10_500));
    }

    [Fact]
    public void AFullTable_OfDevicesStillAnnouncing_RefusesANewOne()
    {
        Forge200(at: 0);
        Forge200(at: 9_000);

        Assert.False(_filter.ShouldPost(Pc(201), 10_000));
        Assert.True(_filter.ShouldPost(Pc(1), 10_000));
    }

    [Fact]
    public void TheTable_NeverHoldsMoreThan200UnpinnedDevices_AndPairedOnesAreNotCounted()
    {
        for (var n = 1; n <= 50; n++)
        {
            _pinned.Add(Pc(9000 + n).DeviceId);
            Assert.True(_filter.ShouldPost(Pc(9000 + n), 0));
        }

        Assert.Equal(0, _filter.UnpinnedTracked);

        // A new id every 20 ms for 20 seconds: the oldest are forgotten as the table fills.
        for (var n = 1; n <= 1000; n++)
        {
            _filter.ShouldPost(Pc(n), n * 20);
            Assert.InRange(_filter.UnpinnedTracked, 1, AnnouncementFilter.MaxDevices);
        }

        Assert.Equal(200, AnnouncementFilter.MaxDevices);
    }
}
