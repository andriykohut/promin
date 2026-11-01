using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

public sealed class RoomWallLockTests
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

    public RoomWallLockTests()
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
    private RoomWallViewModel TenWithSevenOffline()
    {
        for (var n = 1; n <= 10; n++)
        {
            _store.Members.Add(new RoomMember(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, PairedOn, true));
        }

        var vm = new RoomWallViewModel(_store, _discovery, _broadcast, _connections, _pairing, _settings, () => _now, a => a());
        vm.Activate();
        See(1, 2, 3, 4, 5, 6, 8, 9, 10);
        vm.Tick();
        return vm;
    }

    private RoomWallViewModel OnlyTicked(int n)
    {
        var vm = TenWithSevenOffline();
        vm.ClearSelection();
        vm.ToggleSelected(Tile(vm, n));
        return vm;
    }

    private async Task<RoomWallViewModel> OnAir()
    {
        var vm = TenWithSevenOffline();
        await vm.StartAsync();
        return vm;
    }

    private void LockEveryOnlinePc(RoomWallViewModel vm)
    {
        foreach (var n in new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10 })
        {
            _connections.RaiseLock(Id(n), true);
        }

        vm.Tick();
    }

    [Fact]
    public void AReportedLock_ReadsLocked_AndIsNotCountedReady()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        vm.Tick();
        Assert.Equal(PcTileState.Locked, Tile(vm, 1).State);
        Assert.Equal(TileScreen.Locked, Tile(vm, 1).Screen);
        Assert.Equal("Locked", Tile(vm, 1).StatusText);
        Assert.Equal([8, 1, 1], vm.Pills.Select(p => p.Count));
    }

    [Fact]
    public void TheButton_WithNothingTicked_IsGreyedLock()
    {
        var vm = TenWithSevenOffline();
        vm.ClearSelection();
        Assert.False(vm.CanLock);
        Assert.Equal("Lock", vm.LockButtonText);
    }

    [Fact]
    public void TheButton_WithSomeNotLocked_ReadsLock_AndCountsOnlyThose()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        vm.Tick();
        Assert.True(vm.CanLock);
        Assert.False(vm.LockButtonUnlocks);
        Assert.Equal("Lock 8 PCs", vm.LockPanelTitle);
        Assert.Equal("PC-07 is offline and won’t be locked.", vm.LockPanelOfflineLine);
    }

    [Fact]
    public void TheButton_WithEveryTickedPcLocked_ReadsUnlock()
    {
        var vm = TenWithSevenOffline();
        LockEveryOnlinePc(vm);
        Assert.True(vm.CanLock);
        Assert.True(vm.LockButtonUnlocks);
        Assert.Equal("Unlock", vm.LockButtonText);
    }

    [Fact]
    public async Task TheButton_OnAir_IsGreyed()
    {
        var vm = await OnAir();
        Assert.False(vm.CanLock);
        Assert.Equal("Lock", vm.LockButtonText);
    }

    [Fact]
    public void Lock_SendsTheCleanedMessage_OnlyToTickedPcsNotLocked()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        vm.Tick();

        vm.Lock("  Break ");

        Assert.Equal(8, _connections.LocksSent.Count);
        Assert.All(_connections.LocksSent, sent => Assert.Equal("Break", sent.Message));
        Assert.DoesNotContain(_connections.LocksSent, sent => sent.Id == Id(1) || sent.Id == Id(7));
    }

    [Fact]
    public void Unlock_GoesToEveryTickedPc()
    {
        var vm = TenWithSevenOffline();
        LockEveryOnlinePc(vm);

        vm.Unlock();

        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10 }.Select(Id).Order(),
            _connections.UnlocksSent.Order());
    }

    [Fact]
    public void Unlock_WhileSomeAreNotLocked_SendsNothing()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        vm.Tick();

        vm.Unlock();

        Assert.Empty(_connections.UnlocksSent);
    }

    [Fact]
    public void AfterLock_TheTicksStay()
    {
        var vm = TenWithSevenOffline();
        vm.Lock("");
        LockEveryOnlinePc(vm);

        Assert.All(vm.Tiles.Where(t => t.State == PcTileState.Locked), t => Assert.True(t.IsSelected));
        Assert.True(vm.LockButtonUnlocks);
    }

    [Fact]
    public async Task Broadcast_ToALockedPc_ReadsReceiving_ThenLockedAgain()
    {
        var vm = TenWithSevenOffline();
        _connections.RaisePicture(Id(1), Picture1);
        _connections.RaiseLock(Id(1), true);
        vm.Tick();
        _connections.RaisePicture(Id(1), Picture1);
        Assert.Equal(TileScreen.Locked, Tile(vm, 1).Screen);

        await vm.StartAsync();
        Assert.Equal(PcTileState.Receiving, Tile(vm, 1).State);
        Assert.Equal(TileScreen.Picture, Tile(vm, 1).Screen);
        Assert.Equal([9, 1], vm.Pills.Select(p => p.Count));   // receiving, reconnecting (PC-07); none locked

        vm.Stop();
        Assert.Equal(PcTileState.Locked, Tile(vm, 1).State);
        Assert.Equal(TileScreen.Locked, Tile(vm, 1).Screen);
    }

    [Fact]
    public void ALockedPcThatLeavesLocked_WithNoPictureRemembered_ShowsTheGlyph()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        vm.Tick();
        _connections.RaiseLock(Id(1), false);
        vm.Tick();

        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
        Assert.Equal(TileScreen.Glyph, Tile(vm, 1).Screen);
    }

    [Fact]
    public async Task ALockedPcTicked_StillStartsABroadcast()
    {
        var vm = TenWithSevenOffline();
        LockEveryOnlinePc(vm);

        await vm.StartAsync();

        Assert.True(vm.IsOnAir);
        Assert.Equal(9, Assert.Single(_broadcast.Starts).Pcs.Count);
    }

    [Fact]
    public void APcThatDoesNotReport_IsNamedAfterFiveSeconds()
    {
        var vm = OnlyTicked(3);
        vm.Lock("");
        _now += TimeSpan.FromSeconds(4);
        See(3);
        vm.Tick();
        Assert.DoesNotContain("did not lock", vm.BarLine2);
        _now += TimeSpan.FromSeconds(2);
        See(3);
        vm.Tick();
        Assert.Equal("PC-03 did not lock", vm.BarLine2);
        vm.Lock("");
        Assert.DoesNotContain("did not lock", vm.BarLine2);
    }

    [Fact]
    public void APcThatDoesNotUnlock_IsNamedToo()
    {
        var vm = OnlyTicked(3);
        _connections.RaiseLock(Id(3), true);
        vm.Tick();
        vm.Unlock();
        _now += TimeSpan.FromSeconds(6);
        See(3);
        vm.Tick();
        Assert.Equal("PC-03 did not unlock", vm.BarLine2);
    }

    [Fact]
    public void APcThatReportsInTime_IsNotNamed()
    {
        var vm = OnlyTicked(3);
        vm.Lock("");
        _connections.RaiseLock(Id(3), true);
        _now += TimeSpan.FromSeconds(6);
        See(3);
        vm.Tick();
        Assert.DoesNotContain("did not", vm.BarLine2);
    }

    [Fact]
    public void Lock_WithTheTicksGone_SendsNothing()
    {
        var vm = TenWithSevenOffline();
        vm.ClearSelection();
        vm.Lock("x");
        Assert.Empty(_connections.LocksSent);
    }

    [Fact]
    public async Task Lock_OnAir_SendsNothing()
    {
        var vm = await OnAir();
        vm.Lock("x");
        Assert.Empty(_connections.LocksSent);
    }

    [Fact]
    public void AnEndedConnection_ForgetsTheLock_AndTheNextReportBringsItBack()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        _connections.RaiseEnded(Id(1));
        vm.Tick();
        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
        _connections.RaiseLock(Id(1), true);
        vm.Tick();
        Assert.Equal(PcTileState.Locked, Tile(vm, 1).State);
    }

    [Fact]
    public void AnEndedConnection_ForgetsAPendingLockAsk()
    {
        var vm = OnlyTicked(3);
        vm.Lock("");
        _connections.RaiseEnded(Id(3));
        _now += TimeSpan.FromSeconds(6);
        See(3);
        vm.Tick();
        Assert.DoesNotContain("did not", vm.BarLine2);
    }

    [Fact]
    public async Task RemovingALockedPc_UnlocksItBeforeTheConnectionGoes()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        vm.Remove(Tile(vm, 1));
        await _connections.Dropping(Id(1));
        Assert.Equal([Id(1)], _connections.UnlocksSent);
        Assert.Equal([Id(1)], _connections.Dropped);
    }

    [Fact]
    public async Task RemovingAPcThatIsNotLocked_DropsItWithoutAnUnlock()
    {
        var vm = TenWithSevenOffline();
        vm.Remove(Tile(vm, 2));
        await _connections.Dropping(Id(2));
        Assert.Empty(_connections.UnlocksSent);
    }

    [Fact]
    public async Task ALockedPcRemovedOnSetUpDevices_IsUnlockedToo()
    {
        var vm = TenWithSevenOffline();
        _connections.RaiseLock(Id(1), true);
        _store.Members.RemoveAll(m => m.DeviceId == Id(1));

        vm.Activate();

        await _connections.Dropping(Id(1));
        Assert.Equal([Id(1)], _connections.UnlocksSent);
        Assert.Equal([Id(1)], _connections.Dropped);
    }
}
