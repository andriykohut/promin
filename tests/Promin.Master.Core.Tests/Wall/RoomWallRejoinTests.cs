using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

/// <summary>A PC that drops off, or was not there at the start, rejoins the running broadcast by itself.</summary>
public sealed class RoomWallRejoinTests
{
    private static readonly DateOnly PairedOn = new(2026, 9, 22);

    private readonly InMemoryRoomStore _store = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeBroadcastController _broadcast = new();
    private readonly FakePairingClient _pairing = new();
    private readonly InMemoryWallSettings _settings = new();
    private DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    public RoomWallRejoinTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    private static string Id(int n) => $"device-{n:00}";

    private void See(params int[] numbers)
    {
        foreach (var n in numbers)
        {
            _discovery.Raise(new StudentPc(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843));
        }
    }

    private static PcTile Tile(RoomWallViewModel vm, int n) => vm.Tiles.Single(t => t.DeviceId == Id(n));

    private IEnumerable<string> Added => _broadcast.Adds.Select(pc => pc.DeviceId);

    /// <summary>Ten members; the ones in <paramref name="online"/> seen; on air.</summary>
    private async Task<RoomWallViewModel> OnAir(params int[] online)
    {
        for (var n = 1; n <= 10; n++)
        {
            _store.Members.Add(new RoomMember(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, PairedOn, Selected: true));
        }

        var vm = new RoomWallViewModel(_store, _discovery, _broadcast, _pairing, _settings, () => _now, a => a());
        vm.Activate();
        See(online);
        vm.Tick();
        await vm.StartAsync();
        return vm;
    }

    private static readonly int[] AllTen = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>On air with every PC; PC-07 then drops off and is seen again.</summary>
    private async Task<RoomWallViewModel> SevenLostAndBack()
    {
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(7));
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);
        See(7);
        return vm;
    }

    private void Advance(RoomWallViewModel vm, int seconds, params int[] seen)
    {
        _now = _now.AddSeconds(seconds);
        See(seen);
        vm.Tick();
    }

    // ---- the brief's nine --------------------------------------------------------------------

    [Fact]
    public async Task Reconnecting_PcSeenAgain_IsRejoined()
    {
        var vm = await SevenLostAndBack();

        vm.Tick();

        Assert.Equal([Id(7)], Added);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Equal(["10 receiving"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
    }

    [Fact]
    public async Task Rejoin_ShowsTheBackMessage_ForSixSeconds()
    {
        var vm = await SevenLostAndBack();

        vm.Tick();

        Assert.Equal(WallMessageKind.Info, vm.Message?.Kind);
        Assert.Equal("PC-07 is back.", vm.Message?.Title);
        Assert.Equal("It joined the broadcast by itself.", vm.Message?.Text);
        Assert.Null(vm.Message?.ActionLabel);
        Assert.True(vm.IsMessageOpen);

        Advance(vm, 5, AllTen);
        Assert.Equal("PC-07 is back.", vm.MessageTitle);
        Assert.Equal(WallMessageKind.Info, vm.MessageKind);

        Advance(vm, 2, AllTen);
        Assert.Null(vm.Message);
        Assert.False(vm.IsMessageOpen);
    }

    [Fact]
    public async Task BackMessage_ReplacesTheWarning_ThenTheWarningReturns()
    {
        _broadcast.HardwareEncoderAvailable = false;
        var vm = await SevenLostAndBack();
        Assert.Equal(WallMessageKind.Warning, vm.Message?.Kind);

        vm.Tick();
        Assert.Equal("PC-07 is back.", vm.Message?.Title);

        Advance(vm, 7, AllTen);
        Assert.Equal("Quality reduced to 720p, 10 fps.", vm.Message?.Title);
    }

    [Fact]
    public async Task BackMessage_NeverReplacesAnError()
    {
        var vm = await SevenLostAndBack();
        _store.SaveThrows = new RoomStoreException("full", new IOException());
        vm.Rename(Tile(vm, 1), "Front left");
        Assert.Equal(WallMessageKind.Error, vm.Message?.Kind);

        vm.Tick();

        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Equal("Promin could not save this room.", vm.Message?.Title);
    }

    [Fact]
    public async Task FailedRejoin_IsRetried_NoSoonerThanThreeSeconds()
    {
        _broadcast.Unreachable.Add(Id(7));
        var vm = await SevenLostAndBack();

        var attemptsAfterEachSecond = new List<int>();
        for (var second = 0; second <= 7; second++)
        {
            if (second > 0)
            {
                Advance(vm, 1, 7);
            }
            else
            {
                vm.Tick();
            }

            attemptsAfterEachSecond.Add(_broadcast.Adds.Count);
        }

        Assert.Equal([1, 1, 1, 2, 2, 2, 3, 3], attemptsAfterEachSecond);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task ARejoinInFlight_IsNotStartedTwice()
    {
        _broadcast.PendingAdd = new TaskCompletionSource<bool>();
        var vm = await SevenLostAndBack();

        vm.Tick();
        Advance(vm, 3, 7);
        Advance(vm, 3, 7);

        Assert.Equal([Id(7)], Added);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);

        _broadcast.PendingAdd.SetResult(true);

        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Equal("PC-07 is back.", vm.Message?.Title);
    }

    [Fact]
    public async Task APcTheTeacherStopped_IsNotRejoined()
    {
        var vm = await OnAir(AllTen);

        vm.StopSendingTo(Tile(vm, 4));
        Advance(vm, 3, 4);
        Advance(vm, 3, 4);

        Assert.Empty(_broadcast.Adds);
        Assert.Equal(PcTileState.Ready, Tile(vm, 4).State);
    }

    [Fact]
    public async Task APcOfflineAtStart_JoinsWhenItAppears()
    {
        var vm = await OnAir(1, 2, 3, 4, 5, 6, 8, 9, 10);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);

        See(7);
        vm.Tick();

        Assert.Equal([Id(7)], Added);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
    }

    [Fact]
    public void OffAir_NothingIsRejoined()
    {
        for (var n = 1; n <= 10; n++)
        {
            _store.Members.Add(new RoomMember(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, PairedOn, Selected: true));
        }

        var vm = new RoomWallViewModel(_store, _discovery, _broadcast, _pairing, _settings, () => _now, a => a());
        vm.Activate();

        See(7);
        vm.Tick();

        Assert.Empty(_broadcast.Adds);
        Assert.Equal(PcTileState.Ready, Tile(vm, 7).State);
    }

    [Fact]
    public async Task Rejoined_StartsItsDropCountFromZero()
    {
        var vm = await OnAir(AllTen);
        _broadcast.Stats[Id(7)] = new BroadcastStats(12, 500);
        vm.Tick();
        Assert.Equal(PcTileState.DroppingFrames, Tile(vm, 7).State);
        Assert.Equal(12, Tile(vm, 7).FramesDropped);

        _broadcast.RaiseLost(Id(7));
        _broadcast.Stats.Remove(Id(7)); // a new client's counters start at zero
        See(7);
        vm.Tick();

        Assert.Equal(0, Tile(vm, 7).FramesDropped);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
    }

    // ---- edges (carry-ins, section 3) ----------------------------------------------------------

    [Fact]
    public async Task StopBroadcast_WhileARejoinIsInFlight_LeavesThePcOut()
    {
        _broadcast.PendingAdd = new TaskCompletionSource<bool>();
        var vm = await SevenLostAndBack();
        vm.Tick();

        vm.Stop();

        // The attempt is told to give up, so the real controller closes the connection it was opening.
        Assert.True(_broadcast.LastAddToken.IsCancellationRequested);

        _broadcast.PendingAdd.SetResult(true);

        Assert.False(vm.IsOnAir);
        Assert.Equal([Id(7)], _broadcast.StoppedSendingTo);
        Assert.Equal(PcTileState.Ready, Tile(vm, 7).State);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task RemovedWhileARejoinIsInFlight_IsStopped_WhenTheAttemptReturns()
    {
        _broadcast.PendingAdd = new TaskCompletionSource<bool>();
        var vm = await SevenLostAndBack();
        vm.Tick();

        vm.Remove(Tile(vm, 7));
        Assert.Empty(_broadcast.StoppedSendingTo);
        _broadcast.PendingAdd.SetResult(true);

        Assert.Equal([Id(7)], _broadcast.StoppedSendingTo);
        Assert.DoesNotContain(vm.Tiles, t => t.DeviceId == Id(7));
        Assert.Equal(["9 receiving"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task StoppedByTheTeacherWhileARejoinIsInFlight_IsNotShownReceiving()
    {
        _broadcast.PendingAdd = new TaskCompletionSource<bool>();
        var vm = await SevenLostAndBack();
        vm.Tick();

        vm.StopSendingTo(Tile(vm, 7));
        _broadcast.PendingAdd.SetResult(true);

        Assert.Equal([Id(7)], _broadcast.StoppedSendingTo);
        Assert.Equal(PcTileState.Ready, Tile(vm, 7).State);
        Assert.Null(vm.Message);

        Advance(vm, 3, 7);
        Assert.Single(_broadcast.Adds);
    }

    [Fact]
    public async Task AnAttemptThatThrows_IsAFailedAttempt_RetriedAfterThreeSeconds()
    {
        _broadcast.AddThrows = new InvalidOperationException("no route");
        var vm = await SevenLostAndBack();

        vm.Tick();
        Assert.Single(_broadcast.Adds);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);

        Advance(vm, 2, 7);
        Assert.Single(_broadcast.Adds);

        Advance(vm, 1, 7);
        Assert.Equal(2, _broadcast.Adds.Count);
        Assert.True(vm.IsOnAir);
    }

    [Fact]
    public async Task TwoPcsBackInOneTick_AreBothRejoined_AndTheMessageNamesTheLater()
    {
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(4));
        _broadcast.RaiseLost(Id(7));
        See(4, 7);

        vm.Tick();

        Assert.Equal([Id(4), Id(7)], Added);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Equal("PC-07 is back.", vm.Message?.Title);
    }

    [Fact]
    public async Task ARejoinedPcThatIsLostAgain_IsRejoinedAgain()
    {
        var vm = await SevenLostAndBack();
        vm.Tick();
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);

        _broadcast.RaiseLost(Id(7));
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);

        Advance(vm, 1, 7);
        Assert.Single(_broadcast.Adds); // no sooner than three seconds after the last attempt

        Advance(vm, 2, 7);
        Assert.Equal(2, _broadcast.Adds.Count);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
    }

    // ---- the message is about a PC that is being sent to ---------------------------------------

    /// <summary>On air, PC-07 lost, seen again and rejoined: the bar says it is back.</summary>
    private async Task<RoomWallViewModel> SevenIsBack()
    {
        var vm = await SevenLostAndBack();
        vm.Tick();
        Assert.Equal("PC-07 is back.", vm.Message?.Title);
        return vm;
    }

    [Fact]
    public async Task BackMessage_EndsWithTheBroadcast_AndIsNotShownOnTheNext()
    {
        var vm = await SevenIsBack();

        vm.Stop();
        Assert.Null(vm.Message);

        await vm.StartAsync();
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task BackMessage_Ends_WhenThePcIsLostAgain_AndTheWarningReturns()
    {
        _broadcast.HardwareEncoderAvailable = false;
        var vm = await SevenIsBack();

        _broadcast.RaiseLost(Id(7));

        Assert.Equal("Quality reduced to 720p, 10 fps.", vm.Message?.Title);
    }

    [Fact]
    public async Task BackMessage_Ends_WhenTheTeacherStopsSendingToThePc()
    {
        var vm = await SevenIsBack();

        vm.StopSendingTo(Tile(vm, 7));

        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task BackMessage_Ends_WhenThePcIsRemoved()
    {
        var vm = await SevenIsBack();

        vm.Remove(Tile(vm, 7));

        Assert.Null(vm.Message);
    }

    // ---- more edges ----------------------------------------------------------------------------

    [Fact]
    public async Task Reconnecting_ButNotSeenWithinSixSeconds_IsNotTried()
    {
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(7));

        Advance(vm, 6, 1);

        Assert.Empty(_broadcast.Adds);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);

        See(7);
        vm.Tick();

        Assert.Equal([Id(7)], Added);
    }

    [Fact]
    public async Task RejoinedAtANewAddress_IsDialledThere_AndTheAddressIsSavedOnceItWorked()
    {
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(7));
        _discovery.Raise(new StudentPc(Id(7), "PC-07", "192.168.1.99", 17900));
        Assert.Equal(0, _store.Saves);

        vm.Tick();

        Assert.Equal("192.168.1.99", Assert.Single(_broadcast.Adds).LastAddress);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Equal(1, _store.Saves);
        var stored = _store.Members.Single(m => m.DeviceId == Id(7));
        Assert.Equal("192.168.1.99", stored.LastAddress);
        Assert.Equal(17900, stored.Port);
        Assert.Equal("192.168.1.99", Tile(vm, 7).Address);
    }

    [Fact]
    public async Task RememberingAnAddress_KeepsWhatSetUpDevicesChangedInTheRoomMeanwhile()
    {
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(7));

        // Set up devices keeps its own copy of the room; the wall has not read the file again.
        _store.Members.RemoveAll(m => m.DeviceId == Id(4));
        _store.Members.Add(new RoomMember(Id(11), "PC-11", "192.168.1.31", 17843, PairedOn, Selected: true));
        _discovery.Raise(new StudentPc(Id(7), "PC-07", "192.168.1.99", 17900));

        vm.Tick();

        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Equal(1, _store.Saves);
        Assert.DoesNotContain(_store.Members, m => m.DeviceId == Id(4));
        Assert.Contains(_store.Members, m => m.DeviceId == Id(11));
        Assert.Equal("192.168.1.99", _store.Members.Single(m => m.DeviceId == Id(7)).LastAddress);
        Assert.Equal(17900, _store.Members.Single(m => m.DeviceId == Id(7)).Port);
    }

    [Fact]
    public async Task RemovedOnSetUpDevicesWhileItsRejoinIsInFlight_IsStopped_AndNotShownReceiving()
    {
        _broadcast.PendingAdd = new TaskCompletionSource<bool>();
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(7));
        _discovery.Raise(new StudentPc(Id(7), "PC-07", "192.168.1.99", 17900));
        vm.Tick();

        // Set up devices removes the PC, which always takes its pin away; the wall has not read the room again.
        _pairing.Trusted = id => id != Id(7);
        _broadcast.PendingAdd.SetResult(true);

        Assert.Equal([Id(7)], _broadcast.StoppedSendingTo);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);
        Assert.Equal(["9 receiving", "1 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
        Assert.Null(vm.Message);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task ARoomFileThatIsMissingOrEmptied_DoesNotMakeATrustedPcsRejoinCountAsRemoved()
    {
        var vm = await OnAir(AllTen);
        _broadcast.RaiseLost(Id(7));

        // The file is gone, or was set aside as corrupt: the store then reads as an empty room.
        _store.Members.Clear();
        _discovery.Raise(new StudentPc(Id(7), "PC-07", "192.168.1.99", 17900));

        vm.Tick();

        Assert.Equal(PcTileState.Receiving, Tile(vm, 7).State);
        Assert.Empty(_broadcast.StoppedSendingTo);
        Assert.Equal("PC-07 is back.", vm.Message?.Title);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task EndingTheBroadcast_LetsNothingOutOfACancelledAttempt_AndTheNextBroadcastRejoinsAsUsual()
    {
        _broadcast.PendingAdd = new TaskCompletionSource<bool>();
        var vm = await SevenLostAndBack();
        vm.Tick();
        _broadcast.LastAddToken.Register(() => throw new InvalidOperationException("a transport's cancellation callback"));

        vm.Stop();

        Assert.False(vm.IsOnAir);
        _broadcast.PendingAdd.SetResult(false);
        _broadcast.PendingAdd = null;

        await vm.StartAsync();
        _broadcast.RaiseLost(Id(4));
        vm.Tick();

        Assert.Equal(Id(4), _broadcast.Adds[^1].DeviceId);
        Assert.False(_broadcast.LastAddToken.IsCancellationRequested);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
    }
}
