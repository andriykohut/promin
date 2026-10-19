using Promin.Master.Core.Rooms;
using Promin.Master.Core.Sending;

namespace Promin.Master.Core.Tests.Sending;

public sealed class SendTargetsTests
{
    private static SendTarget Pc(int n, bool online) =>
        new(new RoomMember($"device-{n:00}", $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, new DateOnly(2026, 9, 22), true), $"PC-{n:00}", online);

    [Fact]
    public void Current_TakesEachChosenPcAsItIsNow_AndKeepsOneNoLongerInTheRoom()
    {
        var now = new SendTargets([], [Pc(1, online: true), Pc(2, online: false)]);

        var current = now.Current([Pc(1, online: false), Pc(2, online: true), Pc(3, online: true)]);

        Assert.Equal([Pc(1, online: true), Pc(2, online: false), Pc(3, online: true)], current);
    }
}
