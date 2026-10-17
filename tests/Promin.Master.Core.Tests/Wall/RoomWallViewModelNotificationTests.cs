using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

public sealed class RoomWallViewModelNotificationTests
{
    [Fact]
    public async Task IsOnAir_IsRaisedWhenItChanges_NotOnEveryTick()
    {
        var store = new InMemoryRoomStore();
        store.Members.Add(new RoomMember("device-01", "PC-01", "192.168.1.21", 17843, new DateOnly(2026, 9, 22), Selected: true));
        var discovery = new FakeDiscovery();
        var now = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        var vm = new RoomWallViewModel(store, discovery, new FakeBroadcastController(), new FakeRoomConnections(), new FakePairingClient(), new InMemoryWallSettings(), () => now, a => a());
        vm.Activate();
        discovery.Raise(new AgentPc("device-01", "PC-01", "192.168.1.21", 17843));
        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(RoomWallViewModel.IsOnAir) ? 1 : 0;

        vm.Tick();
        vm.Tick();
        Assert.Equal(0, raised);

        await vm.StartAsync();
        Assert.Equal(1, raised);

        now = now.AddSeconds(1);
        vm.Tick();
        Assert.Equal(1, raised);

        vm.Stop();
        Assert.Equal(2, raised);
    }
}
