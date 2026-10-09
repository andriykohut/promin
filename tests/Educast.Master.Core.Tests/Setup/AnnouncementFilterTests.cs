using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Tests.Setup;

public sealed class AnnouncementFilterTests
{
    private readonly AnnouncementFilter _filter = new();

    private static StudentPc Pc(int n, string address = "192.168.1.21", int port = 17843, string name = "PC") =>
        new($"device-{n:000}", name, address, port);

    [Fact]
    public void TheSameAnnouncement_IsPostedOnceASecond()
    {
        Assert.True(_filter.ShouldPost(Pc(1), 0));
        Assert.False(_filter.ShouldPost(Pc(1), 1));
        Assert.False(_filter.ShouldPost(Pc(1), 999));
        Assert.True(_filter.ShouldPost(Pc(1), 1000));
        Assert.False(_filter.ShouldPost(Pc(1), 1999));
    }

    [Fact]
    public void AnAnnouncementThatDiffers_IsPostedAtOnce()
    {
        Assert.True(_filter.ShouldPost(Pc(1), 0));

        Assert.True(_filter.ShouldPost(Pc(1, address: "192.168.1.99"), 1));
        Assert.True(_filter.ShouldPost(Pc(1, address: "192.168.1.99", port: 17900), 2));
        Assert.True(_filter.ShouldPost(Pc(1, address: "192.168.1.99", port: 17900, name: "Front left"), 3));
        Assert.False(_filter.ShouldPost(Pc(1, address: "192.168.1.99", port: 17900, name: "Front left"), 4));
    }

    [Fact]
    public void EachPc_IsCountedByItself()
    {
        Assert.True(_filter.ShouldPost(Pc(1), 0));
        Assert.True(_filter.ShouldPost(Pc(2), 0));
        Assert.False(_filter.ShouldPost(Pc(1), 500));
    }

    [Fact]
    public void AtMost200DevicesAreTracked_OthersAreIgnored_AndTheKnownOnesStillPass()
    {
        for (var n = 1; n <= AnnouncementFilter.MaxDevices; n++)
        {
            Assert.True(_filter.ShouldPost(Pc(n), 0));
        }

        Assert.False(_filter.ShouldPost(Pc(201), 0));
        Assert.False(_filter.ShouldPost(Pc(201), 5000));
        Assert.True(_filter.ShouldPost(Pc(200), 5000));
        Assert.Equal(200, AnnouncementFilter.MaxDevices);
    }
}
