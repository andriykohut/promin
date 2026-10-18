using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;
using Promin.Protocol.Video;

namespace Promin.Master.Core.Tests.Wall;

public sealed class RoomWallViewModelTests
{
    private static readonly DateOnly PairedOn = new(2026, 9, 22);

    private readonly InMemoryRoomStore _store = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeBroadcastController _broadcast = new();
    private readonly FakeRoomConnections _connections = new();
    private readonly FakePairingClient _pairing = new();
    private readonly InMemoryWallSettings _settings = new();
    private DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    public RoomWallViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    private static string Id(int n) => $"device-{n:00}";

    private static RoomMember Member(int n, bool selected = true) =>
        new(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, PairedOn, selected);

    private void Room(int count)
    {
        for (var n = 1; n <= count; n++)
        {
            _store.Members.Add(Member(n));
        }
    }

    private RoomWallViewModel Build(Action<Action>? runOnUiThread = null) =>
        new(_store, _discovery, _broadcast, _connections, _pairing, _settings, () => _now, runOnUiThread ?? (a => a()));

    private RoomWallViewModel Activated(Action<Action>? runOnUiThread = null)
    {
        var vm = Build(runOnUiThread);
        vm.Activate();
        return vm;
    }

    private void See(params int[] numbers)
    {
        foreach (var n in numbers)
        {
            _discovery.Raise(new AgentPc(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843));
        }
    }

    private static PcTile Tile(RoomWallViewModel vm, int n) => vm.Tiles.Single(t => t.DeviceId == Id(n));

    private static RoomMember Stored(InMemoryRoomStore store, int n) => store.Members.Single(m => m.DeviceId == Id(n));

    /// <summary>Ten members, every one seen but PC-07.</summary>
    private RoomWallViewModel TenWithSevenOffline()
    {
        Room(10);
        var vm = Activated();
        See(1, 2, 3, 4, 5, 6, 8, 9, 10);
        vm.Tick();
        return vm;
    }

    private async Task<RoomWallViewModel> OnAir()
    {
        var vm = TenWithSevenOffline();
        await vm.StartAsync();
        return vm;
    }

    // ---- off air -----------------------------------------------------------------------------

    [Fact]
    public void EmptyRoom_SaysSo_AndCannotStart()
    {
        var vm = Activated();

        Assert.True(vm.IsEmptyRoom);
        Assert.Equal("No PCs to broadcast to", vm.BarLine1);
        Assert.Equal("Set up devices to add the first one.", vm.BarLine2);
        Assert.False(vm.CanStart);
        Assert.Empty(vm.Pills);
        Assert.Null(vm.Message);
    }

    [Fact]
    public void OffAir_OnlinePcsAreReady_OthersOffline()
    {
        var vm = TenWithSevenOffline();

        Assert.Equal(9, vm.Tiles.Count(t => t.State == PcTileState.Ready));
        Assert.Equal(PcTileState.Offline, Tile(vm, 7).State);
        Assert.Equal("Offline", Tile(vm, 7).StatusText);
        Assert.Equal(["9 ready", "1 offline"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
        Assert.Equal("9 of 10 PCs selected", vm.BarLine1);
        Assert.Equal("PC-07 is offline. It joins by itself if it comes back.", vm.BarLine2);
        Assert.False(vm.IsOnAir);
        Assert.True(vm.CanStart);
        Assert.Equal(string.Empty, vm.ElapsedText);
    }

    [Fact]
    public void AnOfflinePc_ShowsAnEmptyDisabledBox_ButKeepsItsFlag()
    {
        var vm = TenWithSevenOffline();

        Assert.False(Tile(vm, 7).IsSelected);
        Assert.False(Tile(vm, 7).CanSelect);
        Assert.True(Stored(_store, 7).Selected);
    }

    [Fact]
    public void ItComesBack_SelectedAgain()
    {
        var vm = TenWithSevenOffline();

        See(7);
        vm.Tick();

        Assert.Equal(PcTileState.Ready, Tile(vm, 7).State);
        Assert.True(Tile(vm, 7).IsSelected);
        Assert.True(Tile(vm, 7).CanSelect);
        Assert.Equal("10 of 10 PCs selected", vm.BarLine1);
        Assert.Equal(string.Empty, vm.BarLine2);
    }

    [Fact]
    public void Toggle_SavesTheFlag()
    {
        var vm = TenWithSevenOffline();

        vm.ToggleSelected(Tile(vm, 1));

        Assert.False(Stored(_store, 1).Selected);
        Assert.False(Tile(vm, 1).IsSelected);
        Assert.Equal("8 of 10 PCs selected", vm.BarLine1);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public void SelectAll_And_Clear_OnlyTouchOnlinePcs()
    {
        Room(10);
        _store.Members[6] = Member(7, selected: false);
        var vm = Activated();
        See(1, 2, 3, 4, 5, 6, 8, 9, 10);
        vm.Tick();

        vm.SelectAll();
        Assert.False(Stored(_store, 7).Selected);
        Assert.All(_store.Members.Where(m => m.DeviceId != Id(7)), m => Assert.True(m.Selected));

        vm.ClearSelection();
        Assert.False(Stored(_store, 7).Selected);
        Assert.All(_store.Members, m => Assert.False(m.Selected));
    }

    [Fact]
    public void CannotStart_WithNothingSelected()
    {
        var vm = TenWithSevenOffline();

        vm.ClearSelection();

        Assert.False(vm.CanStart);
        Assert.Equal("0 of 10 PCs selected", vm.BarLine1);
    }

    [Fact]
    public async Task SeenWithANewAddress_IsDialledThere_AndSavedOnceItWorked()
    {
        var vm = TenWithSevenOffline();

        _discovery.Raise(new AgentPc(Id(1), "PC-01", "192.168.1.99", 17900));
        vm.Tick();

        // An announcement is a claim anyone on the network can make: nothing is written for it.
        Assert.Equal(0, _store.Saves);
        Assert.Equal("192.168.1.21", Stored(_store, 1).LastAddress);
        Assert.Equal("192.168.1.21", Tile(vm, 1).Address);

        await vm.StartAsync();

        var dialled = Assert.Single(_broadcast.Starts).Pcs.Single(p => p.DeviceId == Id(1));
        Assert.Equal("192.168.1.99", dialled.LastAddress);
        Assert.Equal(17900, dialled.Port);
        Assert.Equal(1, _store.Saves);
        Assert.Equal("192.168.1.99", Stored(_store, 1).LastAddress);
        Assert.Equal(17900, Stored(_store, 1).Port);
        Assert.Equal("192.168.1.99", Tile(vm, 1).Address);
        Assert.Equal("192.168.1.22", Stored(_store, 2).LastAddress);
    }

    [Fact]
    public async Task AnAnnouncedAddressThatNeverWorks_IsNeverSaved_AndTheStoredOneIsUsedAfterARestart()
    {
        var vm = TenWithSevenOffline();
        _broadcast.Unreachable.Add(Id(1));
        _discovery.Raise(new AgentPc(Id(1), "PC-01", "192.168.1.99", 17900));

        await vm.StartAsync();
        vm.Tick();

        Assert.Equal("192.168.1.99", Assert.Single(_broadcast.Starts).Pcs.Single(p => p.DeviceId == Id(1)).LastAddress);
        Assert.Equal("192.168.1.99", Assert.Single(_broadcast.Adds).LastAddress);
        Assert.Equal(0, _store.Saves);
        Assert.Equal("192.168.1.21", Stored(_store, 1).LastAddress);

        // The console is closed and opened again; the PC announces itself from where it really is.
        _broadcast.Unreachable.Clear();
        var restarted = Activated();
        See(1);
        restarted.Tick();
        await restarted.StartAsync();

        Assert.Equal("192.168.1.21", _broadcast.Starts[^1].Pcs.Single().LastAddress);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public void AnAnnouncement_WhileSetUpDevicesIsOpen_DoesNotOverwriteTheRoomItChanged()
    {
        var vm = TenWithSevenOffline();

        // Set up devices keeps its own copy of the room; it removes one PC and pairs another.
        _store.Members.RemoveAll(m => m.DeviceId == Id(4));
        _store.Members.Add(Member(11));
        _discovery.Raise(new AgentPc(Id(1), "PC-01", "192.168.1.99", 17900));
        vm.Tick();

        Assert.Equal(0, _store.Saves);
        Assert.DoesNotContain(_store.Members, m => m.DeviceId == Id(4));
        Assert.Contains(_store.Members, m => m.DeviceId == Id(11));
    }

    [Fact]
    public void AnAnnouncementForAPcOutsideTheRoom_IsNotKept()
    {
        Room(10);
        var vm = Activated();
        See(11);

        // Paired on Set up devices a moment later: it reads online once it announces itself as a member.
        _store.Members.Add(Member(11));
        vm.Activate();
        Assert.Equal(PcTileState.Offline, Tile(vm, 11).State);

        See(11);
        vm.Tick();
        Assert.Equal(PcTileState.Ready, Tile(vm, 11).State);
    }

    // ---- start -------------------------------------------------------------------------------

    [Fact]
    public async Task Start_SendsToSelectedOnlinePcs_WithTheChosenPreset()
    {
        var vm = TenWithSevenOffline();
        vm.SelectedPreset = QualityPreset.Sd15;

        await vm.StartAsync();

        var start = Assert.Single(_broadcast.Starts);
        Assert.Equal(9, start.Pcs.Count);
        Assert.DoesNotContain(start.Pcs, p => p.DeviceId == Id(7));
        Assert.Equal(QualityPreset.Sd15, start.Preset);
        Assert.True(vm.IsOnAir);
        Assert.Equal(9, vm.Tiles.Count(t => t.State == PcTileState.Receiving));
        Assert.Equal("Receiving", Tile(vm, 1).StatusText);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task Start_APcThatCouldNotBeReached_IsReconnecting()
    {
        Room(10);
        var vm = Activated();
        See(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        vm.Tick();
        _broadcast.Unreachable.Add(Id(4));

        await vm.StartAsync();

        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 4).State);
        Assert.Equal("Reconnecting", Tile(vm, 4).StatusText);
        Assert.Contains(vm.Pills, p => $"{p.Count} {p.Word}" == "1 reconnecting");
    }

    [Fact]
    public async Task Start_ASelectedPcThatIsOffline_IsReconnecting()
    {
        var vm = await OnAir();

        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);
        Assert.Equal(["9 receiving", "1 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
    }

    [Fact]
    public async Task OnAir_SelectionIsFrozen()
    {
        var vm = await OnAir();
        var saves = _store.Saves;

        vm.ToggleSelected(Tile(vm, 1));
        vm.SelectAll();
        vm.ClearSelection();

        Assert.All(vm.Tiles, t => Assert.False(t.CanSelect));
        Assert.All(vm.Tiles, t => Assert.False(t.IsSelected));
        Assert.True(Stored(_store, 1).Selected);
        Assert.Equal(saves, _store.Saves);
    }

    [Fact]
    public async Task OnAir_ARecentDrop_TurnsTheTileAmber_ThenItRecovers()
    {
        var vm = await OnAir();

        _broadcast.Stats[Id(4)] = new BroadcastStats(37, 11_415);
        vm.Tick();

        Assert.Equal(PcTileState.DroppingFrames, Tile(vm, 4).State);
        Assert.Equal("37 frames dropped", Tile(vm, 4).StatusText);
        Assert.Equal(37, Tile(vm, 4).FramesDropped);
        Assert.Equal(11_415, Tile(vm, 4).FramesOffered);
        Assert.Equal("Its connection is slow. The other PCs are not affected.", Tile(vm, 4).DetailSentence);

        _now = _now.AddSeconds(11);
        vm.Tick();

        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
        Assert.Equal(37, Tile(vm, 4).FramesDropped);
        Assert.Equal(string.Empty, Tile(vm, 4).DetailSentence);
    }

    [Fact]
    public async Task OnAir_BarLinesAndElapsed()
    {
        var vm = await OnAir();

        _now = _now.AddMinutes(12).AddSeconds(41);
        _broadcast.Stats[Id(4)] = new BroadcastStats(37, 11_415);
        vm.Tick();

        Assert.Equal("On air", vm.BarLine1);
        Assert.Equal("12:41", vm.ElapsedText);
        Assert.Equal("12:41", Tile(vm, 4).ReceivingForText);
        Assert.Equal("8 PCs receiving well. PC-04 is dropping frames. PC-07 is reconnecting.", vm.BarLine2);
        Assert.Equal(["8 receiving", "1 dropping frames", "1 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
    }

    [Fact]
    public async Task ClientLost_MakesThatTileReconnecting()
    {
        var vm = await OnAir();

        _broadcast.RaiseLost(Id(4));

        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 4).State);
        Assert.Equal(8, vm.Tiles.Count(t => t.State == PcTileState.Receiving));
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 7).State);
    }

    [Fact]
    public async Task StopSendingTo_StopsOnePc_AndKeepsItsFlag()
    {
        var vm = await OnAir();

        vm.StopSendingTo(Tile(vm, 4));

        Assert.Equal([Id(4)], _broadcast.StoppedSendingTo);
        Assert.Equal(PcTileState.Ready, Tile(vm, 4).State);
        Assert.True(Stored(_store, 4).Selected);
        Assert.True(vm.IsOnAir);
        Assert.Equal(["8 receiving", "1 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
    }

    [Fact]
    public async Task Stop_ReturnsEveryTileToOffAir()
    {
        var vm = await OnAir();

        vm.Stop();

        Assert.Equal(1, _broadcast.Stops);
        Assert.False(vm.IsOnAir);
        Assert.Equal(9, vm.Tiles.Count(t => t.State == PcTileState.Ready));
        Assert.Equal(PcTileState.Offline, Tile(vm, 7).State);
        Assert.True(vm.CanStart);
        Assert.Equal(string.Empty, vm.ElapsedText);
    }

    [Fact]
    public async Task ANewBroadcast_StartsItsDropCountFromZero()
    {
        var vm = await OnAir();
        _broadcast.Stats[Id(4)] = new BroadcastStats(37, 11_415);
        vm.Tick();
        vm.Stop();
        _broadcast.Stats.Clear();

        await vm.StartAsync();
        vm.Tick();

        Assert.Equal(0, Tile(vm, 4).FramesDropped);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
    }

    // ---- quality and messages ----------------------------------------------------------------

    [Fact]
    public void NoHardwareEncoder_ForcesTheFallbackPreset_AndWarns()
    {
        _broadcast.HardwareEncoderAvailable = false;
        Room(10);
        var vm = Activated();
        See(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        vm.Tick();

        Assert.Equal(QualityPreset.SoftwareFallback, vm.SelectedPreset);
        Assert.False(vm.CanChooseQuality);
        Assert.Equal(WallMessageKind.Warning, vm.Message?.Kind);
        Assert.Equal("Quality reduced to 720p, 10 fps.", vm.Message?.Title);
        Assert.Equal("This PC has no hardware video encoder, so Promin is encoding in software.", vm.Message?.Text);
        Assert.Null(vm.Message?.ActionLabel);
    }

    [Fact]
    public void NothingReachable_ShowsTheError_AheadOfTheWarning()
    {
        _broadcast.HardwareEncoderAvailable = false;
        Room(10);
        var vm = Activated();

        Assert.Equal(WallMessageKind.Error, vm.Message?.Kind);
        Assert.Equal("No PCs can be reached.", vm.Message?.Title);
        Assert.Equal("Check that they are switched on and on the same network as this PC.", vm.Message?.Text);
        Assert.Equal("Set up devices", vm.Message?.ActionLabel);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void TheBar_HasPlainValues_ThatChangeWithTheMessage_BothWays()
    {
        Room(1);
        var vm = Activated();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(vm.IsMessageOpen);
        Assert.Equal("No PCs can be reached.", vm.MessageTitle);
        Assert.Equal("Check that they are switched on and on the same network as this PC.", vm.MessageText);
        Assert.Equal("Set up devices", vm.MessageActionLabel);
        Assert.True(vm.HasMessageAction);
        Assert.Equal(WallMessageKind.Error, vm.MessageKind);

        See(1);
        vm.Tick();

        Assert.Null(vm.Message);
        Assert.False(vm.IsMessageOpen);
        Assert.Equal(string.Empty, vm.MessageTitle);
        Assert.Equal(string.Empty, vm.MessageText);
        Assert.Equal(string.Empty, vm.MessageActionLabel);
        Assert.False(vm.HasMessageAction);
        Assert.Contains(nameof(RoomWallViewModel.IsMessageOpen), raised);
        Assert.Contains(nameof(RoomWallViewModel.MessageTitle), raised);

        raised.Clear();
        _now = _now.AddSeconds(7);
        vm.Tick();

        Assert.True(vm.IsMessageOpen);
        Assert.Equal("No PCs can be reached.", vm.MessageTitle);
        Assert.Contains(nameof(RoomWallViewModel.IsMessageOpen), raised);
    }

    [Fact]
    public void Quality_CanBeChosenOffAir()
    {
        var vm = TenWithSevenOffline();

        Assert.True(vm.CanChooseQuality);
        Assert.Equal(QualityPreset.All, vm.Presets);
        Assert.Equal(QualityPreset.Default, vm.SelectedPreset);
    }

    [Fact]
    public async Task Quality_CannotBeChangedOnAir()
    {
        var vm = await OnAir();

        vm.SelectedPreset = QualityPreset.Sd10;

        Assert.False(vm.CanChooseQuality);
        Assert.Equal(QualityPreset.Default, vm.SelectedPreset);
    }

    [Fact]
    public void Quality_IsRememberedAcrossRuns()
    {
        var first = TenWithSevenOffline();
        first.SelectedPreset = QualityPreset.Sd15;

        var second = Build();
        Assert.Equal(QualityPreset.Sd15, second.SelectedPreset);

        _settings.QualityLabel = "8K, 240 fps";
        var third = Build();
        Assert.Equal(QualityPreset.Default, third.SelectedPreset);
    }

    // ---- rename, remove ----------------------------------------------------------------------

    [Fact]
    public async Task Remove_OnAir_StopsSendingAndForgetsThePc()
    {
        var vm = await OnAir();

        vm.Remove(Tile(vm, 4));

        Assert.Equal([Id(4)], _broadcast.StoppedSendingTo);
        Assert.Equal([Id(4)], _pairing.Unpaired);
        Assert.DoesNotContain(_store.Members, m => m.DeviceId == Id(4));
        Assert.DoesNotContain(vm.Tiles, t => t.DeviceId == Id(4));
        Assert.Equal(["8 receiving", "1 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
    }

    [Fact]
    public void Remove_OffAir_ForgetsThePc_WithoutTouchingTheBroadcast()
    {
        var vm = TenWithSevenOffline();

        vm.Remove(Tile(vm, 7));

        Assert.Empty(_broadcast.StoppedSendingTo);
        Assert.Equal([Id(7)], _pairing.Unpaired);
        Assert.Equal(9, vm.Tiles.Count);
        Assert.Equal("9 of 9 PCs selected", vm.BarLine1);
    }

    [Fact]
    public void Rename_TrimsAndSaves()
    {
        var vm = TenWithSevenOffline();

        vm.Rename(Tile(vm, 7), "  Front left ");

        Assert.Equal("Front left", Tile(vm, 7).Name);
        Assert.Equal("Front left", Stored(_store, 7).DisplayName);
        Assert.Equal("Front left is offline. It joins by itself if it comes back.", vm.BarLine2);
    }

    [Fact]
    public void Rename_ToNothing_ChangesNothing()
    {
        var vm = TenWithSevenOffline();

        vm.Rename(Tile(vm, 7), "   ");

        Assert.Equal("PC-07", Tile(vm, 7).Name);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public void ASaveThatFails_RevertsTheChange_AndSaysSo()
    {
        var vm = TenWithSevenOffline();
        _store.SaveThrows = new RoomStoreException("full", new IOException());

        vm.ToggleSelected(Tile(vm, 1));

        Assert.True(Tile(vm, 1).IsSelected);
        Assert.True(Stored(_store, 1).Selected);
        Assert.Equal("Promin could not save this room.", vm.Message?.Title);

        _store.SaveThrows = null;
        vm.ToggleSelected(Tile(vm, 1));

        Assert.False(Tile(vm, 1).IsSelected);
        Assert.Null(vm.Message);
    }

    [Fact]
    public void AnUnreadableRoom_SaysSo_AndCannotStart()
    {
        _store.LoadThrows = new RoomStoreException("locked", new IOException());

        var vm = Activated();

        Assert.True(vm.IsEmptyRoom);
        Assert.Equal("Promin could not read this room.", vm.Message?.Title);
        Assert.False(vm.CanStart);
    }

    // ---- threads -----------------------------------------------------------------------------

    [Fact]
    public void DiscoveryEvents_GoThroughTheUiDispatcher()
    {
        var queue = new Queue<Action>();
        Room(1);
        var vm = Activated(queue.Enqueue);

        See(1);
        vm.Tick();
        Assert.Equal(PcTileState.Offline, Tile(vm, 1).State);

        while (queue.TryDequeue(out var action))
        {
            action();
        }

        vm.Tick();
        Assert.Equal(PcTileState.Ready, Tile(vm, 1).State);
    }

    [Fact]
    public async Task ClientLost_GoesThroughTheUiDispatcher()
    {
        var queue = new Queue<Action>();
        Room(2);
        var vm = Activated(queue.Enqueue);
        See(1, 2);
        while (queue.TryDequeue(out var action))
        {
            action();
        }

        vm.Tick();
        await vm.StartAsync();

        _broadcast.RaiseLost(Id(1));
        Assert.Equal(PcTileState.Receiving, Tile(vm, 1).State);

        while (queue.TryDequeue(out var action))
        {
            action();
        }

        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 1).State);
    }

    // ---- the picture source fails (carry-ins, section 4) --------------------------------------

    [Fact]
    public async Task SourceFails_ReturnsTheWallToOffAir_AndSaysSo_UntilTheNextStart()
    {
        var vm = await OnAir();

        _broadcast.RaiseFailed();

        Assert.False(vm.IsOnAir);
        Assert.Equal(0, _broadcast.Stops);
        Assert.Equal(9, vm.Tiles.Count(t => t.State == PcTileState.Ready));
        Assert.Equal(WallMessageKind.Error, vm.Message?.Kind);
        Assert.Equal("The broadcast stopped.", vm.Message?.Title);
        Assert.Equal("This PC could not capture or encode its screen. Start the broadcast again.", vm.Message?.Text);
        Assert.True(vm.CanStart);

        await vm.StartAsync();

        Assert.True(vm.IsOnAir);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task StartThatThrows_StaysOffAir_AndSaysSo()
    {
        var vm = TenWithSevenOffline();
        _broadcast.StartThrows = new InvalidOperationException("no capture device");

        await vm.StartAsync();

        Assert.False(vm.IsOnAir);
        Assert.Equal("The broadcast stopped.", vm.Message?.Title);
        Assert.Equal(9, vm.Tiles.Count(t => t.State == PcTileState.Ready));
        Assert.True(vm.CanStart);
    }

    // ---- while the start is still connecting ---------------------------------------------------

    [Fact]
    public async Task Remove_WhileConnecting_IsNotSentTo_WhenTheStartReturns()
    {
        var vm = TenWithSevenOffline();
        _broadcast.PendingStart = new TaskCompletionSource<IReadOnlyCollection<string>>();
        var starting = vm.StartAsync();

        vm.Remove(Tile(vm, 4));
        Assert.Empty(_broadcast.StoppedSendingTo);

        _broadcast.PendingStart.SetResult([]);
        await starting;

        Assert.True(vm.IsOnAir);
        Assert.Equal([Id(4)], _broadcast.StoppedSendingTo);
        Assert.DoesNotContain(vm.Tiles, t => t.DeviceId == Id(4));
        Assert.Equal(["8 receiving", "1 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
        Assert.Equal([Id(4)], _pairing.Unpaired);
    }

    [Fact]
    public async Task UnpairedWhileConnecting_IsNotSentTo_WhenTheStartReturns()
    {
        var vm = TenWithSevenOffline();
        _broadcast.PendingStart = new TaskCompletionSource<IReadOnlyCollection<string>>();
        var starting = vm.StartAsync();

        // Removed on Set up devices, which takes its pin away; the wall has not read the room again.
        _pairing.Trusted = id => id != Id(4);
        _discovery.Raise(new AgentPc(Id(4), "PC-04", "192.168.1.99", 17900));
        _broadcast.PendingStart.SetResult([]);
        await starting;

        Assert.True(vm.IsOnAir);
        Assert.Equal([Id(4)], _broadcast.StoppedSendingTo);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 4).State);
        Assert.Equal(["8 receiving", "2 reconnecting"], vm.Pills.Select(p => $"{p.Count} {p.Word}"));
        Assert.Null(vm.Message);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task LostWhileConnecting_IsReconnecting_WhenTheStartReturns_AndIsRejoined()
    {
        var vm = TenWithSevenOffline();
        _broadcast.PendingStart = new TaskCompletionSource<IReadOnlyCollection<string>>();
        var starting = vm.StartAsync();

        // It answered, and its connection went before the start had every answer.
        _broadcast.RaiseLost(Id(4));
        _broadcast.PendingStart.SetResult([]);
        await starting;

        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 4).State);
        Assert.Equal(8, vm.Tiles.Count(t => t.State == PcTileState.Receiving));

        vm.Tick();

        Assert.Equal(Id(4), Assert.Single(_broadcast.Adds).DeviceId);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
    }

    [Fact]
    public async Task WhileConnecting_NothingCanBeStartedOrChanged()
    {
        var vm = TenWithSevenOffline();
        _broadcast.PendingStart = new TaskCompletionSource<IReadOnlyCollection<string>>();
        var starting = vm.StartAsync();

        Assert.False(vm.IsOnAir);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanChooseQuality);
        Assert.All(vm.Tiles, t => Assert.False(t.CanSelect));

        await vm.StartAsync();
        vm.ToggleSelected(Tile(vm, 1));
        vm.SelectAll();
        vm.SelectedPreset = QualityPreset.Sd10;
        vm.SelectedSharpness = vm.SharpnessChoices.Single(c => c.Value == Sharpness.Sharp);

        Assert.Single(_broadcast.Starts);
        Assert.True(Stored(_store, 1).Selected);
        Assert.Equal(0, _store.Saves);
        Assert.Equal(QualityPreset.Default, vm.SelectedPreset);
        Assert.Equal(Sharpness.Standard, vm.SelectedSharpness.Value);

        _broadcast.PendingStart.SetResult([]);
        await starting;
        Assert.True(vm.IsOnAir);
    }

    // ---- what a screen reader hears -----------------------------------------------------------

    [Fact]
    public async Task Names_SayWhichPc_AndItsState()
    {
        var vm = TenWithSevenOffline();

        Assert.Equal("Select PC-04", Tile(vm, 4).SelectBoxName);
        Assert.Equal("Details for PC-04", Tile(vm, 4).DetailsButtonName);
        Assert.Equal("PC-04, Ready", Tile(vm, 4).TileName);
        Assert.Equal("PC-07, Offline", Tile(vm, 7).TileName);
        Assert.Equal("Quality, 1080p, 15 fps, 6.2 Mbit/s", vm.QualityPickerName);

        vm.Rename(Tile(vm, 4), "Front left");
        await vm.StartAsync();

        Assert.Equal("Select Front left", Tile(vm, 4).SelectBoxName);
        Assert.Equal("Front left, Receiving", Tile(vm, 4).TileName);
        Assert.Equal("PC-07, Reconnecting", Tile(vm, 7).TileName);
    }

    // ---- activate again (back from Set up devices) ---------------------------------------------

    [Fact]
    public async Task Activate_OnAir_WithAnUnreadableRoom_KeepsSending_AndSaysSo()
    {
        var vm = await OnAir();
        _store.LoadThrows = new RoomStoreException("locked", new IOException());

        vm.Activate();

        Assert.True(vm.IsOnAir);
        Assert.Empty(_broadcast.StoppedSendingTo);
        Assert.Equal(10, vm.Tiles.Count);
        Assert.Equal(9, vm.Tiles.Count(t => t.State == PcTileState.Receiving));
        Assert.Equal("Promin could not read this room.", vm.Message?.Title);
    }

    [Fact]
    public async Task Activate_OnAir_FollowsTheRoom_AndStopsSendingToAPcRemovedElsewhere()
    {
        var vm = await OnAir();
        _store.Members.RemoveAll(m => m.DeviceId == Id(4));
        _store.Members[0] = _store.Members[0] with { DisplayName = "Front left" };
        _store.Members.Add(Member(11));

        vm.Activate();

        Assert.True(vm.IsOnAir);
        Assert.Equal([Id(4)], _broadcast.StoppedSendingTo);
        Assert.DoesNotContain(vm.Tiles, t => t.DeviceId == Id(4));
        Assert.Equal("Front left", Tile(vm, 1).Name);
        Assert.Equal(PcTileState.Receiving, Tile(vm, 1).State);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 11).State);
    }

    // ---- Amendment 2: sharpness ----------------------------------------------------------------

    [Fact]
    public void Sharpness_DefaultsToStandard()
    {
        var vm = TenWithSevenOffline();

        Assert.Equal(Sharpness.Standard, vm.SelectedSharpness.Value);
        Assert.Equal([Sharpness.Light, Sharpness.Standard, Sharpness.Sharp], vm.SharpnessChoices.Select(c => c.Value));
    }

    [Fact]
    public void SharpnessChoices_ShowTheRealRate_ForTheChosenQuality()
    {
        var vm = TenWithSevenOffline();
        vm.SelectedPreset = QualityPreset.Hd15;

        Assert.Equal(
            ["Light, 3.1 Mbit/s", "Standard, 6.2 Mbit/s", "Sharp, 9.3 Mbit/s"],
            vm.SharpnessChoices.Select(c => c.Text));
    }

    [Fact]
    public void ChangingQuality_RecomputesTheRates()
    {
        var vm = TenWithSevenOffline();

        vm.SelectedPreset = QualityPreset.Sd10;

        Assert.Equal(
            ["Light, 0.9 Mbit/s", "Standard, 1.8 Mbit/s", "Sharp, 2.8 Mbit/s"],
            vm.SharpnessChoices.Select(c => c.Text));
        Assert.Equal("Standard, 1.8 Mbit/s", vm.SelectedSharpness.Text);
    }

    [Fact]
    public void TheRate_UsesThisPcsRealEncodeSize()
    {
        _broadcast.DisplaySize = new PixelSize(1366, 768);
        var vm = TenWithSevenOffline();
        vm.SelectedPreset = QualityPreset.Hd15;

        Assert.Equal("Standard, 3.1 Mbit/s", vm.SelectedSharpness.Text);
    }

    [Fact]
    public void QualityButtonText_NamesQualityAndRate()
    {
        var vm = TenWithSevenOffline();
        vm.SelectedPreset = QualityPreset.Hd15;

        Assert.Equal("1080p, 15 fps, 6.2 Mbit/s", vm.QualityButtonText);
    }

    [Fact]
    public void NetworkLoad_TotalsOverSelectedOnlinePcs()
    {
        var vm = TenWithSevenOffline();

        Assert.Equal("6.2 Mbit/s to each PC, about 56 Mbit/s for 9 PCs.", vm.NetworkLoadText);
    }

    [Fact]
    public void NetworkLoad_ForOnePc_AndForNone()
    {
        Room(1);
        var vm = Activated();
        See(1);
        vm.Tick();

        Assert.Equal("6.2 Mbit/s to 1 PC.", vm.NetworkLoadText);

        vm.ClearSelection();

        Assert.Equal(string.Empty, vm.NetworkLoadText);
    }

    [Fact]
    public async Task Start_PassesTheChosenSharpness()
    {
        var vm = TenWithSevenOffline();
        vm.SelectedSharpness = vm.SharpnessChoices.Single(c => c.Value == Sharpness.Sharp);

        await vm.StartAsync();

        Assert.Equal(Sharpness.Sharp, Assert.Single(_broadcast.Starts).Sharpness);
    }

    [Fact]
    public void Sharpness_IsRememberedAcrossRuns()
    {
        var first = TenWithSevenOffline();
        first.SelectedSharpness = first.SharpnessChoices.Single(c => c.Value == Sharpness.Light);

        var second = Build();
        Assert.Equal(Sharpness.Light, second.SelectedSharpness.Value);

        _settings.SharpnessName = "Ultra";
        var third = Build();
        Assert.Equal(Sharpness.Standard, third.SelectedSharpness.Value);
    }

    [Fact]
    public async Task Sharpness_CannotBeChangedOnAir()
    {
        var vm = await OnAir();

        vm.SelectedSharpness = vm.SharpnessChoices.Single(c => c.Value == Sharpness.Sharp);

        Assert.Equal(Sharpness.Standard, vm.SelectedSharpness.Value);
    }

    [Fact]
    public void SoftwareEncoding_StillLetsSharpnessBeChosen()
    {
        _broadcast.HardwareEncoderAvailable = false;
        var vm = TenWithSevenOffline();

        vm.SelectedSharpness = vm.SharpnessChoices.Single(c => c.Value == Sharpness.Sharp);

        Assert.Equal(Sharpness.Sharp, vm.SelectedSharpness.Value);
        Assert.Equal("720p, 10 fps, 2.8 Mbit/s", vm.QualityButtonText);
    }

    private static readonly byte[] Picture1 = [0xFF, 0xD8, 1];
    private static readonly byte[] Picture2 = [0xFF, 0xD8, 2];

    [Fact]
    public void Tick_WantsEveryOnlinePc_AtTheAddressItAnnounced_AndNoOfflineOne()
    {
        Room(3);
        var vm = Activated();
        _discovery.Raise(new AgentPc(Id(1), "PC-01", "10.0.0.5", 17843));
        See(2);

        vm.Tick();

        Assert.Equal([Id(1), Id(2)], _connections.Wanted.Select(m => m.DeviceId));
        Assert.Equal("10.0.0.5", _connections.Wanted[0].LastAddress);
    }

    [Fact]
    public void APicture_ShowsOnItsTile()
    {
        var vm = TenWithSevenOffline();

        _connections.RaisePicture(Id(1), Picture1);

        Assert.Equal(TileScreen.Picture, Tile(vm, 1).Screen);
        Assert.Same(Picture1, Tile(vm, 1).Picture);
        Assert.Equal(TileScreen.Glyph, Tile(vm, 2).Screen);
    }

    [Fact]
    public void AnEmptyPicture_ShowsScreenNotAvailable_AndTheNextPictureClearsIt()
    {
        var vm = TenWithSevenOffline();

        _connections.RaisePicture(Id(1), []);
        Assert.Equal(TileScreen.NotAvailable, Tile(vm, 1).Screen);
        Assert.Null(Tile(vm, 1).Picture);

        _connections.RaisePicture(Id(1), Picture2);
        Assert.Equal(TileScreen.Picture, Tile(vm, 1).Screen);
        Assert.Same(Picture2, Tile(vm, 1).Picture);
    }

    [Fact]
    public void AnEndedConnection_DropsThePicture()
    {
        var vm = TenWithSevenOffline();
        _connections.RaisePicture(Id(1), Picture1);

        _connections.RaiseEnded(Id(1));

        Assert.Equal(TileScreen.Glyph, Tile(vm, 1).Screen);
        Assert.Null(Tile(vm, 1).Picture);
    }

    [Fact]
    public void APcThatGoesOffline_LosesItsPicture()
    {
        var vm = TenWithSevenOffline();
        _connections.RaisePicture(Id(1), Picture1);

        _now = _now.AddSeconds(7);
        See(2, 3, 4, 5, 6, 8, 9, 10);
        vm.Tick();

        Assert.Equal(PcTileState.Offline, Tile(vm, 1).State);
        Assert.Equal(TileScreen.Glyph, Tile(vm, 1).Screen);
        Assert.Null(Tile(vm, 1).Picture);
    }

    [Fact]
    public async Task OnAir_AReceivingPc_KeepsItsPicture_AReconnectingOneLosesIt()
    {
        var vm = await OnAir();
        _connections.RaisePicture(Id(1), Picture1);
        _connections.RaisePicture(Id(4), Picture2);

        _broadcast.RaiseLost(Id(4));

        Assert.Equal(PcTileState.Receiving, Tile(vm, 1).State);
        Assert.Equal(TileScreen.Picture, Tile(vm, 1).Screen);
        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 4).State);
        Assert.Equal(TileScreen.Glyph, Tile(vm, 4).Screen);
    }

    [Fact]
    public async Task APicture_WhileReconnecting_IsNotShown()
    {
        var vm = await OnAir();
        _broadcast.RaiseLost(Id(4));

        _connections.RaisePicture(Id(4), Picture1);

        Assert.Equal(PcTileState.Reconnecting, Tile(vm, 4).State);
        Assert.Equal(TileScreen.Glyph, Tile(vm, 4).Screen);
        Assert.Null(Tile(vm, 4).Picture);
    }

    [Fact]
    public async Task APictureThatArrivedWhileReconnecting_Shows_OnceThePcIsReceivingAgain()
    {
        var vm = await OnAir();
        _broadcast.RaiseLost(Id(4));
        _connections.RaisePicture(Id(4), Picture1);

        vm.Tick();

        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
        Assert.Equal(TileScreen.Picture, Tile(vm, 4).Screen);
        Assert.Same(Picture1, Tile(vm, 4).Picture);
    }

    [Fact]
    public async Task APictureRememberedWhileReconnecting_IsForgotten_WhenItsConnectionEnds()
    {
        var vm = await OnAir();
        _broadcast.RaiseLost(Id(4));
        _connections.RaisePicture(Id(4), Picture1);
        _connections.RaiseEnded(Id(4));

        vm.Tick();

        Assert.Equal(PcTileState.Receiving, Tile(vm, 4).State);
        Assert.Equal(TileScreen.Glyph, Tile(vm, 4).Screen);
        Assert.Null(Tile(vm, 4).Picture);
    }

    [Fact]
    public void APicture_ForAPcNotOnTheWall_IsIgnored()
    {
        var vm = TenWithSevenOffline();

        _connections.RaisePicture("device-99", Picture1);
        _connections.RaiseEnded("device-99");

        Assert.All(vm.Tiles, t => Assert.Equal(TileScreen.Glyph, t.Screen));
    }

    [Fact]
    public void Remove_DropsThePcsConnection()
    {
        var vm = TenWithSevenOffline();

        vm.Remove(Tile(vm, 3));

        Assert.Equal([Id(3)], _connections.Dropped);
    }

    // ---- who a send can go to -----------------------------------------------------------------

    [Fact]
    public void SendTargets_OffAir_SelectedAreTheTickedOnlinePcs_AllIsEveryPc()
    {
        var vm = TenWithSevenOffline();
        vm.ToggleSelected(Tile(vm, 1));

        var targets = vm.SendTargets();

        Assert.Equal([2, 3, 4, 5, 6, 8, 9, 10], targets.Selected.Select(t => int.Parse(t.Member.DeviceId[^2..])));
        Assert.All(targets.Selected, t => Assert.True(t.Online));
        Assert.Equal(10, targets.All.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"PC-{n:00}"), targets.All.Select(t => t.Name));
        Assert.Equal(Enumerable.Range(1, 10).Select(n => n != 7), targets.All.Select(t => t.Online));
    }

    [Fact]
    public void SendTargets_CarryTheAddressEachPcLastAnnouncedFrom()
    {
        var vm = TenWithSevenOffline();
        _discovery.Raise(new AgentPc(Id(2), "PC-02", "10.0.0.2", 17900));

        var targets = vm.SendTargets();

        Assert.Equal(("10.0.0.2", 17900), (targets.Selected[1].Member.LastAddress, targets.Selected[1].Member.Port));
        Assert.Equal("10.0.0.2", targets.All[1].Member.LastAddress);
        Assert.Equal("192.168.1.21", targets.All[0].Member.LastAddress);
        Assert.Equal("192.168.1.22", Stored(_store, 2).LastAddress);
    }

    [Fact]
    public async Task SendTargets_OnAir_SelectedAreThePcsBeingSentTo()
    {
        var vm = await OnAir();
        vm.StopSendingTo(Tile(vm, 3));
        _broadcast.RaiseLost(Id(4));

        var targets = vm.SendTargets();

        Assert.Equal([1, 2, 5, 6, 8, 9, 10], targets.Selected.Select(t => int.Parse(t.Member.DeviceId[^2..])));
        Assert.Equal(10, targets.All.Count);
        Assert.False(targets.All.Single(t => t.Name == "PC-07").Online);
    }
}
