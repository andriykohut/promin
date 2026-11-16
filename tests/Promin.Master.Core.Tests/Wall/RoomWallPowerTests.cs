using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;

namespace Promin.Master.Core.Tests.Wall;

public sealed class RoomWallPowerTests
{
    private static readonly DateOnly PairedOn = new(2026, 9, 22);

    private readonly InMemoryRoomStore _store = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeBroadcastController _broadcast = new();
    private readonly FakeRoomConnections _connections = new();
    private readonly FakePairingClient _pairing = new();
    private readonly InMemoryWallSettings _settings = new();
    private DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    public RoomWallPowerTests()
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

    /// <summary>PC-03 alone ticked, ordered to restart, and answered yes.</summary>
    private RoomWallViewModel ThreeSaidYes()
    {
        var vm = OnlyTicked(3);
        vm.Power(PowerAction.Restart, vm.PowerTargets());
        _connections.RaisePower(Id(3), true);
        return vm;
    }

    [Fact]
    public void TheButton_WithNothingTicked_IsGreyed()
    {
        var vm = TenWithSevenOffline();
        vm.ClearSelection();
        Assert.False(vm.CanPower);
    }

    [Fact]
    public async Task TheButton_OnAir_IsGreyed()
    {
        var vm = TenWithSevenOffline();
        await vm.StartAsync();
        Assert.False(vm.CanPower);
    }

    [Fact]
    public void TheMenu_CountsTheTickedOnlinePcs()
    {
        var vm = TenWithSevenOffline();
        Assert.True(vm.CanPower);
        Assert.Equal("Shut down 9 PCs", vm.ShutDownMenuText);
        Assert.Equal("Restart 9 PCs", vm.RestartMenuText);
    }

    [Fact]
    public void TheConfirmation_NamesOnePc()
    {
        var vm = OnlyTicked(3);
        Assert.Equal("Shut down PC-03?", vm.PowerConfirmTitle(PowerAction.ShutDown));
        Assert.Equal("Unsaved work on it will be lost.", vm.PowerConfirmText);
    }

    [Fact]
    public void TheConfirmation_CountsSeveralPcs()
    {
        var vm = TenWithSevenOffline();
        Assert.Equal("Restart 9 PCs?", vm.PowerConfirmTitle(PowerAction.Restart));
        Assert.Equal("Unsaved work on them will be lost.", vm.PowerConfirmText);
    }

    [Fact]
    public void Power_SendsTheOrderToEachTickedOnlinePc()
    {
        var vm = TenWithSevenOffline();
        vm.Power(PowerAction.ShutDown, vm.PowerTargets());
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10 }.Select(n => (Id(n), PowerAction.ShutDown)),
            _connections.PowersSent);
    }

    [Fact]
    public void AYes_ReadsOfflineAtOnce_AndDropsTheConnection()
    {
        var vm = ThreeSaidYes();
        Assert.Equal(PcTileState.Offline, Tile(vm, 3).State);
        Assert.False(Tile(vm, 3).CanSelect);
        Assert.Contains(Id(3), _connections.Dropped);
    }

    [Fact]
    public void AGoingDownPc_IsNotDialledAgain_WhileItStillAnnounces()
    {
        var vm = ThreeSaidYes();
        var wanted = _connections.Wanted.Count(m => m.DeviceId == Id(3));

        _now += TimeSpan.FromSeconds(2);
        See(3);
        vm.Tick();

        Assert.Equal(wanted, _connections.Wanted.Count(m => m.DeviceId == Id(3)));
        Assert.Equal(PcTileState.Offline, Tile(vm, 3).State);
    }

    [Fact]
    public void ARestartedPc_ReadsReadyOnceItReturns()
    {
        var vm = ThreeSaidYes();

        _now += TimeSpan.FromSeconds(7);
        vm.Tick();
        See(3);
        vm.Tick();

        Assert.Equal(PcTileState.Ready, Tile(vm, 3).State);
    }

    [Fact]
    public void APcThatSaysYesButStaysUp_ReadsReadyAfterAMinute()
    {
        var vm = ThreeSaidYes();
        void TwoSecondsLater()
        {
            _now += TimeSpan.FromSeconds(2);
            See(3);
            vm.Tick();
        }

        for (var i = 0; i < 29; i++)
        {
            TwoSecondsLater();
        }

        Assert.Equal(PcTileState.Offline, Tile(vm, 3).State);

        TwoSecondsLater();
        TwoSecondsLater();

        Assert.Equal(PcTileState.Ready, Tile(vm, 3).State);
    }

    [Fact]
    public void ANo_NamesThePcAtOnce()
    {
        var vm = OnlyTicked(3);
        vm.Power(PowerAction.ShutDown, vm.PowerTargets());

        _connections.RaisePower(Id(3), false);

        Assert.Equal("PC-03 did not shut down", vm.BarLine2);
        Assert.Equal(PcTileState.Ready, Tile(vm, 3).State);
    }

    [Fact]
    public void NoAnswerInFiveSeconds_NamesThePc()
    {
        var vm = OnlyTicked(3);
        vm.Power(PowerAction.Restart, vm.PowerTargets());

        _now += TimeSpan.FromSeconds(5);
        vm.Tick();

        Assert.Equal("PC-03 did not restart", vm.BarLine2);
    }

    [Fact]
    public void ALateYes_TakesThePcOffline_AndClearsItsName()
    {
        var vm = OnlyTicked(3);
        vm.Power(PowerAction.ShutDown, vm.PowerTargets());
        _now += TimeSpan.FromSeconds(5);
        vm.Tick();
        Assert.Equal("PC-03 did not shut down", vm.BarLine2);

        _connections.RaisePower(Id(3), true);

        Assert.Equal(PcTileState.Offline, Tile(vm, 3).State);
        Assert.DoesNotContain("did not", vm.BarLine2, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedPc_IsNeverUnlockedOnTheWay()
    {
        var vm = OnlyTicked(3);
        _connections.RaiseLock(Id(3), true);
        vm.Tick();

        vm.Power(PowerAction.ShutDown, vm.PowerTargets());
        _connections.RaisePower(Id(3), true);

        Assert.Empty(_connections.UnlocksSent);
        Assert.Contains(Id(3), _connections.Dropped);
    }

    [Fact]
    public async Task PowerToTheControlledPc_EndsControlFirst_WithNoLostLine()
    {
        var vm = TenWithSevenOffline();
        _connections.NextSession = new FakeControlSession();
        Assert.NotNull(await vm.StartControlAsync(Tile(vm, 1)));
        var ordersWhenEnded = -1;
        vm.ControlMustEnd += () => ordersWhenEnded = _connections.PowersSent.Count;

        vm.Power(PowerAction.Restart, vm.PowerTargets());
        _connections.RaisePower(Id(1), true);

        Assert.Equal(0, ordersWhenEnded);
        Assert.Null(vm.ControlledName);
        Assert.DoesNotContain("Lost", vm.BarLine2, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNextOrder_ClearsTheLine()
    {
        var vm = OnlyTicked(3);
        vm.Power(PowerAction.ShutDown, vm.PowerTargets());
        _connections.RaisePower(Id(3), false);
        Assert.Equal("PC-03 did not shut down", vm.BarLine2);

        vm.Power(PowerAction.ShutDown, vm.PowerTargets());

        Assert.DoesNotContain("did not", vm.BarLine2, StringComparison.Ordinal);
    }

    [Fact]
    public void APcThatComesOnlineDuringTheConfirmation_IsNotSentTheOrder()
    {
        var vm = TenWithSevenOffline();
        var confirmed = vm.PowerTargets();

        See(7);
        vm.Tick();
        vm.Power(PowerAction.ShutDown, confirmed);

        Assert.Equal(confirmed.Select(id => (id, PowerAction.ShutDown)), _connections.PowersSent);
    }

    [Fact]
    public async Task AnOrder_ClearsAnEarlierControlFailure_SoItsAnswerShows()
    {
        var vm = OnlyTicked(3);
        Assert.Null(await vm.StartControlAsync(Tile(vm, 5)));
        Assert.Equal("Could not start controlling PC-05.", vm.BarLine2);

        vm.Power(PowerAction.ShutDown, vm.PowerTargets());
        _connections.RaisePower(Id(3), false);

        Assert.Equal("PC-03 did not shut down", vm.BarLine2);
    }
}
