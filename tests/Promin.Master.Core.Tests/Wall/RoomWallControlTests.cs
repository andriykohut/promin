using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

public sealed class RoomWallControlTests
{
    private static readonly DateOnly PairedOn = new(2026, 9, 22);
    private static readonly byte[] Picture1 = [0xFF, 0xD8, 1];

    private readonly InMemoryRoomStore _store = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeBroadcastController _broadcast = new();
    private readonly FakeRoomConnections _connections = new();
    private readonly FakePairingClient _pairing = new();
    private readonly InMemoryWallSettings _settings = new();
    private DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    public RoomWallControlTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    private static string Id(int n) => $"device-{n:00}";

    private static PcTile Tile(RoomWallViewModel vm, int n) => vm.Tiles.Single(t => t.DeviceId == Id(n));

    private void See(params int[] numbers)
    {
        foreach (var n in numbers)
        {
            _discovery.Raise(new AgentPc(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843));
        }
    }

    /// <summary>Ten members, every one ticked, every one seen but PC-07.</summary>
    private RoomWallViewModel TenWithSevenOffline(Func<string, bool>? isThisPc = null)
    {
        for (var n = 1; n <= 10; n++)
        {
            _store.Members.Add(new RoomMember(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, PairedOn, true));
        }

        var vm = new RoomWallViewModel(_store, _discovery, _broadcast, _connections, _pairing, _settings, () => _now, a => a(), isThisPc);
        vm.Activate();
        See(1, 2, 3, 4, 5, 6, 8, 9, 10);
        vm.Tick();
        return vm;
    }

    private async Task<RoomWallViewModel> Controlling(int n)
    {
        var vm = TenWithSevenOffline();
        _connections.NextSession = new FakeControlSession();
        Assert.NotNull(await vm.StartControlAsync(Tile(vm, n)));
        return vm;
    }

    [Fact]
    public void CanControl_AnOnlinePc() => Assert.True(Tile(TenWithSevenOffline(), 1).CanControl);

    [Fact]
    public void CanControl_NotAnOfflinePc() => Assert.False(Tile(TenWithSevenOffline(), 7).CanControl);

    [Fact]
    public async Task CanControl_NothingOnAir()
    {
        var vm = TenWithSevenOffline();
        await vm.StartAsync();
        Assert.False(Tile(vm, 1).CanControl);
    }

    [Fact]
    public void CanControl_NotThisPc()
    {
        var vm = TenWithSevenOffline(address => address == "192.168.1.21");
        Assert.False(Tile(vm, 1).CanControl);
        Assert.True(Tile(vm, 2).CanControl);
    }

    [Fact]
    public void CanControl_ATickedOrUntickedPc_Alike()
    {
        var vm = TenWithSevenOffline();
        vm.ToggleSelected(Tile(vm, 1));
        Assert.False(Tile(vm, 1).IsSelected);
        Assert.True(Tile(vm, 1).CanControl);
        Assert.True(Tile(vm, 2).CanControl);
    }

    [Fact]
    public async Task StartControl_MarksTheTile_AndGreysStart()
    {
        var vm = TenWithSevenOffline();
        _connections.NextSession = new FakeControlSession();
        Assert.NotNull(await vm.StartControlAsync(Tile(vm, 1)));
        Assert.Equal(PcTileState.Controlled, Tile(vm, 1).State);
        Assert.Equal("Being controlled", Tile(vm, 1).StatusText);
        Assert.False(vm.CanStart);
        Assert.Equal("PC-01", vm.ControlledName);
    }

    [Fact]
    public async Task StartControl_ThatFails_SaysSo_AndLeavesTheTile()
    {
        var vm = TenWithSevenOffline();
        _connections.NextSession = null;
        Assert.Null(await vm.StartControlAsync(Tile(vm, 1)));
        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
        Assert.Null(vm.ControlledName);
        Assert.True(vm.CanStart);
        Assert.Equal("Could not start controlling PC-01.", vm.BarLine2);
    }

    [Fact]
    public async Task StartControl_OnASecondPc_EndsTheFirst()
    {
        var vm = await Controlling(1);
        var startsWhenEnded = -1;
        vm.ControlMustEnd += () => startsWhenEnded = _connections.ControlStarts.Count;
        _connections.NextSession = new FakeControlSession();
        Assert.NotNull(await vm.StartControlAsync(Tile(vm, 2)));
        Assert.Equal(1, startsWhenEnded);
        Assert.Equal(2, _connections.ControlStarts.Count);
        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
        Assert.Equal(PcTileState.Controlled, Tile(vm, 2).State);
        Assert.Equal("PC-02", vm.ControlledName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALateEndOfTheFirstControl_LeavesTheSecondAlone(bool lost)
    {
        var vm = await Controlling(1);
        var ended = 0;
        vm.ControlMustEnd += () => ended++;
        _connections.NextSession = new FakeControlSession();
        await vm.StartControlAsync(Tile(vm, 2));
        vm.ControlEnded(Id(1), lost);
        Assert.Equal(1, ended);
        Assert.Equal(PcTileState.Controlled, Tile(vm, 2).State);
        Assert.Equal("PC-02", vm.ControlledName);
        Assert.Equal(WallText.OfflineLine(["PC-07"]), vm.BarLine2);
    }

    [Fact]
    public async Task StartControl_OnTheControlledPc_ReturnsNull()
    {
        var vm = await Controlling(1);
        var ended = 0;
        vm.ControlMustEnd += () => ended++;
        Assert.Null(await vm.StartControlAsync(Tile(vm, 1)));
        Assert.Equal(0, ended);
        Assert.Equal("PC-01", vm.ControlledName);
    }

    [Fact]
    public async Task ControlEnded_ReturnsTheTileToReady()
    {
        var vm = await Controlling(1);
        vm.ControlEnded(Id(1), lost: false);
        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
        Assert.Null(vm.ControlledName);
        Assert.True(vm.CanStart);
        Assert.Equal(WallText.OfflineLine(["PC-07"]), vm.BarLine2);
    }

    [Fact]
    public async Task TheConnectionEnding_EndsControl_WithTheLostLine()
    {
        var vm = await Controlling(1);
        var ended = 0;
        vm.ControlMustEnd += () => ended++;
        _connections.RaiseEnded(Id(1));
        Assert.Equal(1, ended);
        vm.ControlEnded(Id(1), lost: true);
        Assert.Equal("Lost the connection to PC-01. Control has ended.", vm.BarLine2);
    }

    [Fact]
    public async Task AfterALostConnection_ThePcCanBeControlledAgain()
    {
        var vm = await Controlling(1);
        _connections.RaiseEnded(Id(1));
        vm.ControlEnded(Id(1), lost: true);
        See(1);
        vm.Tick();
        _connections.NextSession = new FakeControlSession();
        Assert.NotNull(await vm.StartControlAsync(Tile(vm, 1)));
        Assert.Equal(PcTileState.Controlled, Tile(vm, 1).State);
        Assert.Equal(WallText.OfflineLine(["PC-07"]), vm.BarLine2);
    }

    [Fact]
    public async Task AControlledTile_KeepsItsLastPicture()
    {
        var vm = TenWithSevenOffline();
        _connections.RaisePicture(Id(1), Picture1);
        Assert.Equal(Picture1, Tile(vm, 1).Picture);
        _connections.NextSession = new FakeControlSession();
        await vm.StartControlAsync(Tile(vm, 1));
        byte[] newer = [0xFF, 0xD8, 2];
        _connections.RaisePicture(Id(1), newer);
        Assert.Equal(Picture1, Tile(vm, 1).Picture);
        vm.ControlEnded(Id(1), lost: false);
        Assert.Equal(newer, Tile(vm, 1).Picture);
    }

    [Fact]
    public async Task LockingTheControlledPc_EndsControlFirst()
    {
        var vm = await Controlling(1);
        var locksWhenEnded = -1;
        vm.ControlMustEnd += () => locksWhenEnded = _connections.LocksSent.Count;
        vm.Lock("Back soon");
        Assert.Equal(0, locksWhenEnded);
        Assert.Contains(_connections.LocksSent, l => l.Id == Id(1));
    }

    [Fact]
    public async Task LockingOtherPcs_LeavesControlAlone()
    {
        var vm = await Controlling(1);
        var ended = 0;
        vm.ControlMustEnd += () => ended++;
        vm.ClearSelection();
        vm.ToggleSelected(Tile(vm, 2));
        vm.Lock("Back soon");
        Assert.Equal(0, ended);
        Assert.Equal("PC-01", vm.ControlledName);
    }

    [Fact]
    public async Task RemovingTheControlledPc_EndsControl()
    {
        var vm = await Controlling(1);
        var ended = 0;
        vm.ControlMustEnd += () => ended++;
        vm.Remove(Tile(vm, 1));
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task ALockedPcControlled_ReadsControlled_ThenUnlocked()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        _connections.NextSession = new FakeControlSession();
        await vm.StartControlAsync(Tile(vm, 1));
        Assert.Equal(PcTileState.Controlled, Tile(vm, 1).State);
        _connections.RaiseLock(Id(1), false);
        vm.ControlEnded(Id(1), lost: false);
        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
    }
}
