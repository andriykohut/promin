using System.Collections.Specialized;
using System.ComponentModel;
using Educast.Master.Core;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;
using Educast.Master.Core.Tests.TestDoubles;

namespace Educast.Master.Core.Tests.Setup;

public sealed class SetupViewModelTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    private readonly FakePairingClient _client = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly InMemoryRoomStore _store = new();
    private readonly ConsoleHealth _health = new();

    private SetupViewModel Activated(Action<Action>? runOnUiThread = null)
    {
        var vm = new SetupViewModel(_client, _discovery, _store, _health, () => Today, runOnUiThread ?? (a => a()));
        vm.Activate();
        return vm;
    }

    private static string Id(int n) => $"device-{n:00}";

    private static StudentPc Pc(int n, string? address = null) =>
        new(Id(n), $"PC-{n:00}", address ?? $"192.168.1.{20 + n}", 17843);

    private void Raise(params int[] numbers)
    {
        foreach (var n in numbers)
        {
            _discovery.Raise(Pc(n));
        }
    }

    private static SetupRow Row(SetupViewModel vm, int n) => vm.Rows.Single(r => r.DeviceId == Id(n));

    [Fact]
    public void SeenPcs_AppearAsNotPaired_InNameOrder()
    {
        var vm = Activated();

        Raise(2, 1);

        Assert.Equal(["PC-01", "PC-02"], vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => Assert.Equal(SetupRowStatus.NotPaired, r.Status));
        Assert.Equal("2 found, none paired yet", vm.CountText);
        Assert.True(vm.ShowCodeCard);
        Assert.False(vm.NothingFound);
    }

    [Fact]
    public void RoomMembers_LoadAsInRoom()
    {
        _store.Members.Add(new RoomMember(Id(1), "PC-01", "192.168.1.21", 17843, Today, Selected: true));

        var vm = Activated();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(SetupRowStatus.InRoom, row.Status);
        Assert.Equal("1 paired", vm.CountText);
        Assert.False(vm.ShowCodeCard);
        Assert.Equal(1, _discovery.Started);
    }

    [Fact]
    public void SeeingAKnownPcAgain_UpdatesItsAddress_NotItsStatus()
    {
        _store.Members.Add(new RoomMember(Id(1), "PC-01", "192.168.1.21", 17843, Today, Selected: true));
        var vm = Activated();

        _discovery.Raise(Pc(1, "192.168.1.99"));

        var row = Assert.Single(vm.Rows);
        Assert.Equal(SetupRowStatus.InRoom, row.Status);
        Assert.Equal("192.168.1.99", row.Address);
    }

    [Fact]
    public void ARepeatedAnnouncement_ChangesNothingVisible()
    {
        var vm = Activated();
        Raise(1);
        var rowEvents = new List<string?>();
        var listEvents = 0;
        vm.Rows[0].PropertyChanged += (_, e) => rowEvents.Add(e.PropertyName);
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, _) => listEvents++;

        Raise(1);

        Assert.Empty(rowEvents);
        Assert.Equal(0, listEvents);
    }

    [Fact]
    public void Discovery_KeepsAtMost200Pcs()
    {
        var vm = Activated();

        for (var n = 1; n <= 201; n++)
        {
            _discovery.Raise(new StudentPc($"flood-{n:000}", $"Flood {n:000}", $"10.0.{n / 256}.{n % 256}", 17843));
        }

        Assert.Equal(200, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r.DeviceId == "flood-201");
    }

    [Fact]
    public void CanPairAll_NeedsAValidCode()
    {
        var vm = Activated();
        Raise(Enumerable.Range(1, 10).ToArray());

        vm.CodeText = "48291";
        Assert.False(vm.CanPairAll);

        vm.CodeText = "482 913";
        Assert.True(vm.CanPairAll);
        Assert.Equal("Pair all 10", vm.PairAllLabel);
    }

    [Fact]
    public void CanPairAll_IsFalse_WhileTheConsoleHasALocalFault()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        _health.LocalFault = ConsoleHealth.CertificateFaultMessage;

        Assert.False(vm.CanPairAll);
        Assert.Contains(nameof(SetupViewModel.CanPairAll), changes);
        Assert.False(Row(vm, 1).CanAct);

        _health.LocalFault = null;

        Assert.True(vm.CanPairAll);
        Assert.True(Row(vm, 1).CanAct);
    }

    [Fact]
    public async Task PairAll_PairsEveryPc_InRowOrder_WithTheNormalisedCode()
    {
        var vm = Activated();
        Raise(3, 1, 2);
        vm.CodeText = "482-913";

        await vm.PairAllAsync();

        Assert.Equal(
            [(Id(1), "482913"), (Id(2), "482913"), (Id(3), "482913")],
            _client.Calls);
        Assert.All(vm.Rows, r => Assert.Equal(SetupRowStatus.InRoom, r.Status));
        Assert.Equal(3, _store.Members.Count);
        Assert.All(_store.Members, m => Assert.True(m.Selected));
        Assert.All(_store.Members, m => Assert.Equal(Today, m.PairedOn));
        Assert.Equal(["PC-01", "PC-02", "PC-03"], _store.Members.Select(m => m.DisplayName));
        Assert.Equal(
            new SetupBanner(
                SetupBannerKind.Success,
                "All 3 PCs are in this room.",
                "You only do this once. They reconnect by themselves from now on."),
            vm.Banner);
        Assert.False(vm.ShowCodeCard);
        Assert.Equal("3 paired", vm.CountText);
        Assert.Equal("Pair all", vm.PairAllLabel);
        Assert.False(vm.CanPairAll);
    }

    [Fact]
    public async Task PairAll_ShowsProgressWhileItRuns()
    {
        var vm = Activated();
        Raise(1, 2, 3);
        vm.CodeText = "482913";
        _client.HoldDeviceId = Id(2);

        var run = vm.PairAllAsync();
        await _client.Reached;

        Assert.Equal(
            [SetupRowStatus.InRoom, SetupRowStatus.Pairing, SetupRowStatus.Waiting],
            vm.Rows.Select(r => r.Status));
        Assert.True(vm.IsPairing);
        Assert.Equal("Pairing 2 of 3", vm.PairAllLabel);
        Assert.Equal("1 paired so far", vm.CountText);
        Assert.False(vm.CanPairAll);
        Assert.Null(vm.Banner);
        Assert.Single(_store.Members);

        _client.Release();
        await run;

        Assert.False(vm.IsPairing);
        Assert.All(vm.Rows, r => Assert.Equal(SetupRowStatus.InRoom, r.Status));
    }

    [Fact]
    public async Task PairAll_WithMixedResults_NamesEachReason()
    {
        var vm = Activated();
        Raise(Enumerable.Range(1, 10).ToArray());
        vm.CodeText = "482913";
        _client.Results[Id(4)] = PairResult.WrongCode;
        _client.Results[Id(7)] = PairResult.Unreachable;

        await vm.PairAllAsync();

        Assert.Equal(SetupRowStatus.WrongCode, Row(vm, 4).Status);
        Assert.Equal(SetupRowStatus.Unreachable, Row(vm, 7).Status);
        Assert.Equal(8, vm.Rows.Count(r => r.Status == SetupRowStatus.InRoom));
        Assert.Equal(
            new SetupBanner(SetupBannerKind.Warning, "8 of 10 PCs are in this room.", "2 need attention below."),
            vm.Banner);
        Assert.Equal("Try the 2 again", vm.PairAllLabel);
        Assert.Equal("8 paired, 2 not", vm.CountText);
        Assert.True(vm.ShowCodeCard);
        Assert.True(vm.CanPairAll);
    }

    [Fact]
    public async Task OneFailure_UsesTheSingular()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";
        _client.Results[Id(2)] = PairResult.CannotPairNow;

        await vm.PairAllAsync();

        Assert.Equal(SetupRowStatus.CannotPairNow, Row(vm, 2).Status);
        Assert.Equal("1 needs attention below.", vm.Banner?.Message);
        Assert.Equal("1 of 2 PCs are in this room.", vm.Banner?.Title);
        Assert.Equal("Try the 1 again", vm.PairAllLabel);
    }

    [Fact]
    public async Task NoBanner_WhenEveryPairingFailed()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "111111";
        _client.Results[Id(1)] = PairResult.WrongCode;

        await vm.PairAllAsync();

        Assert.Null(vm.Banner);
        Assert.Equal("1 found, none paired yet", vm.CountText);
        Assert.Equal("Try the 1 again", vm.PairAllLabel);
    }

    [Fact]
    public async Task TryAgain_TouchesOnlyTheFailures()
    {
        var vm = Activated();
        Raise(Enumerable.Range(1, 10).ToArray());
        vm.CodeText = "482913";
        _client.Results[Id(4)] = PairResult.WrongCode;
        _client.Results[Id(7)] = PairResult.Unreachable;
        await vm.PairAllAsync();
        _client.Results.Clear();
        _client.Calls.Clear();

        await vm.PairAllAsync();

        Assert.Equal([(Id(4), "482913"), (Id(7), "482913")], _client.Calls);
        Assert.All(vm.Rows, r => Assert.Equal(SetupRowStatus.InRoom, r.Status));
        Assert.Equal(SetupBannerKind.Success, vm.Banner?.Kind);
    }

    [Fact]
    public async Task Cancel_LetsTheCurrentPcFinish_AndReturnsTheRestToNotPaired()
    {
        var vm = Activated();
        Raise(1, 2, 3);
        vm.CodeText = "482913";
        _client.HoldDeviceId = Id(1);
        var run = vm.PairAllAsync();
        await _client.Reached;

        vm.Cancel();

        Assert.Equal(SetupRowStatus.Pairing, Row(vm, 1).Status);
        Assert.Equal(SetupRowStatus.NotPaired, Row(vm, 2).Status);
        Assert.Equal(SetupRowStatus.NotPaired, Row(vm, 3).Status);
        Assert.False(_client.Tokens[0].IsCancellationRequested);

        _client.Release();
        await run;

        Assert.Equal(SetupRowStatus.InRoom, Row(vm, 1).Status);
        Assert.Equal(SetupRowStatus.NotPaired, Row(vm, 2).Status);
        Assert.Equal(SetupRowStatus.NotPaired, Row(vm, 3).Status);
        Assert.Single(_client.Calls);
        Assert.False(vm.IsPairing);
        Assert.Equal("Pair all 2", vm.PairAllLabel);
    }

    [Fact]
    public async Task PairOne_WithAnotherCode_LeavesTheMainFieldAlone()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";

        await vm.PairOneAsync(Row(vm, 1), "111 222");

        Assert.Equal([(Id(1), "111222")], _client.Calls);
        Assert.Equal("482913", vm.CodeText);
        Assert.Equal(SetupRowStatus.InRoom, Row(vm, 1).Status);
        Assert.Single(_store.Members);
    }

    [Fact]
    public async Task PairOne_WithoutAnOverride_UsesTheMainField()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482 913";

        await vm.PairOneAsync(Row(vm, 1));

        Assert.Equal([(Id(1), "482913")], _client.Calls);
    }

    [Fact]
    public async Task PairOne_WithAnInvalidOverride_DoesNothing()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";

        await vm.PairOneAsync(Row(vm, 1), "12");

        Assert.Empty(_client.Calls);
        Assert.Equal(SetupRowStatus.NotPaired, Row(vm, 1).Status);
    }

    [Fact]
    public async Task PairOne_DoesNotLightTheMainButton_ButBlocksItWhileItRuns()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";
        _client.HoldDeviceId = Id(1);

        var run = vm.PairOneAsync(Row(vm, 1));
        await _client.Reached;

        Assert.Equal(SetupRowStatus.Pairing, Row(vm, 1).Status);
        Assert.False(vm.IsPairing);
        Assert.False(vm.CanPairAll);
        Assert.False(Row(vm, 2).CanAct);

        _client.Release();
        await run;

        Assert.True(vm.CanPairAll);
        Assert.True(Row(vm, 2).CanAct);
    }

    [Fact]
    public async Task TheCode_IsNeverWrittenToTheStore()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482-913";

        await vm.PairAllAsync();

        Assert.True(_store.Saves > 0);
        Assert.DoesNotContain("482913", _store.Serialised);
        Assert.DoesNotContain("482-913", _store.Serialised);
    }

    [Fact]
    public async Task Remove_UnpairsAndForgets_ButKeepsTheRowWhileThePcIsSeen()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        await vm.PairAllAsync();
        var row = Row(vm, 1);

        vm.Remove(row);

        Assert.Equal([Id(1)], _client.Unpaired);
        Assert.Empty(_store.Members);
        Assert.Same(row, Assert.Single(vm.Rows));
        Assert.Equal(SetupRowStatus.NotPaired, row.Status);
        Assert.Null(vm.Banner);
        Assert.True(vm.ShowCodeCard);
    }

    [Fact]
    public void Remove_DropsTheRow_WhenThePcIsNotBeingSeen()
    {
        _store.Members.Add(new RoomMember(Id(1), "PC-01", "192.168.1.21", 17843, Today, Selected: true));
        var vm = Activated();

        vm.Remove(vm.Rows[0]);

        Assert.Equal([Id(1)], _client.Unpaired);
        Assert.Empty(_store.Members);
        Assert.Empty(vm.Rows);
        Assert.True(vm.NothingFound);
    }

    [Fact]
    public async Task Rename_TrimsAndSaves_AndIgnoresAnEmptyName()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        await vm.PairAllAsync();
        var row = Row(vm, 1);

        vm.Rename(row, "  Front left  ");
        Assert.Equal("Front left", row.Name);
        Assert.Equal("Front left", Assert.Single(_store.Members).DisplayName);

        vm.Rename(row, "   ");
        Assert.Equal("Front left", row.Name);
        Assert.Equal("Front left", Assert.Single(_store.Members).DisplayName);
    }

    [Fact]
    public async Task Rename_CutsTo64Characters_AndKeepsTheRowsInNameOrder()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";
        await vm.PairAllAsync();

        vm.Rename(Row(vm, 2), "Aisle " + new string('x', 70));

        Assert.Equal(64, Row(vm, 2).Name.Length);
        Assert.Equal([Id(2), Id(1)], vm.Rows.Select(r => r.DeviceId));
    }

    [Fact]
    public async Task ARenamedPc_KeepsItsName_WhenItIsSeenAgain()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        await vm.PairAllAsync();
        vm.Rename(Row(vm, 1), "Front left");

        Raise(1);

        Assert.Equal("Front left", Row(vm, 1).Name);
    }

    [Fact]
    public void DiscoveryEvents_GoThroughTheUiDispatcher()
    {
        var queue = new Queue<Action>();
        var vm = Activated(queue.Enqueue);

        Raise(1);
        Assert.Empty(vm.Rows);

        while (queue.TryDequeue(out var action))
        {
            action();
        }

        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Deactivate_StopsDiscovery_AndCancelsPairing()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";
        _client.HoldDeviceId = Id(1);
        var run = vm.PairAllAsync();
        await _client.Reached;

        vm.Deactivate();

        Assert.Equal(1, _discovery.Stopped);
        Assert.True(_client.Tokens[0].IsCancellationRequested);

        _client.Release();
        await run;
        Assert.False(vm.IsPairing);
    }

    [Fact]
    public void Deactivate_StopsListening_ToDiscoveryAndToHealth()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        var changes = 0;
        vm.PropertyChanged += (_, _) => changes++;

        vm.Deactivate();
        Raise(2);
        _health.LocalFault = "x";

        Assert.Single(vm.Rows);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void AnAnnouncement_DeliveredAfterDeactivate_IsIgnored()
    {
        var queue = new Queue<Action>();
        var vm = Activated(queue.Enqueue);
        Raise(1);

        vm.Deactivate();
        while (queue.TryDequeue(out var action))
        {
            action();
        }

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Activate_ReloadsTheRoom_AndRestartsDiscovery()
    {
        var vm = Activated();
        Raise(1);
        vm.Deactivate();
        _store.Members.Add(new RoomMember(Id(2), "PC-02", "192.168.1.22", 17843, Today, Selected: true));

        vm.Activate();

        Assert.Equal([Id(2)], vm.Rows.Select(r => r.DeviceId));
        Assert.Equal(2, _discovery.Started);
    }
}
