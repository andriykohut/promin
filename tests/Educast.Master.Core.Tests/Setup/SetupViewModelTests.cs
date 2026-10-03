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

        _health.LocalFault = LocalFault.UnusableCertificate;

        Assert.False(vm.CanPairAll);
        Assert.Contains(nameof(SetupViewModel.CanPairAll), changes);
        Assert.False(Row(vm, 1).CanAct);
        Assert.False(Row(vm, 1).CanPairNow);
        Assert.True(Row(vm, 1).CanEdit);

        _health.LocalFault = null;

        Assert.True(vm.CanPairAll);
        Assert.True(Row(vm, 1).CanAct);
        Assert.True(Row(vm, 1).CanPairNow);
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
        // Two rows, so that the room is not complete afterwards (a complete room clears the code).
        var vm = Activated();
        Raise(1, 2);
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
        _health.LocalFault = LocalFault.NoSecureTransport;

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

    // Fix round 1 — A1: nothing a save or a pairing does may leave a row stuck or escape.

    private static readonly SetupBanner SaveError = new(
        SetupBannerKind.Error, "Educast could not save this room.", "Check that this PC's disk is not full, then try again.");

    [Fact]
    public async Task APairingThatThrows_MakesThatRowCannotPairNow_AndTheRestStillPair()
    {
        var vm = Activated();
        Raise(1, 2, 3);
        vm.CodeText = "482913";
        _client.Throws[Id(2)] = new InvalidOperationException("a programming error in the transport");

        await vm.PairAllAsync();

        Assert.Equal(SetupRowStatus.InRoom, Row(vm, 1).Status);
        Assert.Equal(SetupRowStatus.CannotPairNow, Row(vm, 2).Status);
        Assert.Equal(SetupRowStatus.InRoom, Row(vm, 3).Status);
        Assert.Equal(3, _client.Calls.Count);
        Assert.Null(vm.StoreError);
        Assert.False(vm.IsPairing);
    }

    [Fact]
    public async Task ASaveThatFailsAfterASuccessfulPairing_UnpairsThePc_AndShowsTheSaveError()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        _store.SaveThrows = new IOException("disk full");

        await vm.PairAllAsync();

        Assert.Equal(SetupRowStatus.CannotPairNow, Row(vm, 1).Status);
        Assert.Equal([Id(1)], _client.Unpaired);
        Assert.Empty(_store.Members);
        Assert.Equal(SaveError, vm.StoreError);
    }

    [Fact]
    public async Task PairAll_NeverLeavesARowWaitingOrPairing_WhateverHappens()
    {
        var vm = Activated();
        Raise(1, 2, 3, 4);
        vm.CodeText = "482913";
        _client.Throws[Id(1)] = new InvalidOperationException("boom");
        _client.Results[Id(2)] = PairResult.Unreachable;
        _store.SaveThrows = new IOException("disk full");

        await vm.PairAllAsync();

        Assert.DoesNotContain(vm.Rows, r => r.Status is SetupRowStatus.Waiting or SetupRowStatus.Pairing);
        Assert.False(vm.IsPairing);
        Assert.Equal(4, _client.Calls.Count);
    }

    [Fact]
    public async Task Rename_WhenTheSaveFails_KeepsTheOldName_AndShowsTheSaveError()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        await vm.PairAllAsync();
        _store.SaveThrows = new IOException("disk full");

        vm.Rename(Row(vm, 1), "Front left");

        Assert.Equal("PC-01", Row(vm, 1).Name);
        Assert.Equal("PC-01", Assert.Single(_store.Members).DisplayName);
        Assert.Equal(SaveError, vm.StoreError);
    }

    [Fact]
    public async Task Remove_WhenTheSaveFails_KeepsThePcInTheRoom_AndShowsTheSaveError()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";
        await vm.PairAllAsync();
        _store.SaveThrows = new IOException("disk full");

        vm.Remove(Row(vm, 1));

        Assert.Equal(SetupRowStatus.InRoom, Row(vm, 1).Status);
        Assert.Single(_store.Members);
        Assert.Empty(_client.Unpaired);
        Assert.Equal(SaveError, vm.StoreError);
    }

    [Fact]
    public async Task TheSaveError_ClearsOnTheNextSuccessfulSave()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";
        await vm.PairAllAsync();
        _store.SaveThrows = new IOException("disk full");
        vm.Rename(Row(vm, 1), "Front left");
        Assert.NotNull(vm.StoreError);

        _store.SaveThrows = null;
        vm.Rename(Row(vm, 1), "Front left");

        Assert.Null(vm.StoreError);
        Assert.Equal("Front left", Row(vm, 1).Name);
    }

    [Fact]
    public async Task Deactivate_DuringAPairing_LeavesNoRowWaitingOrPairing()
    {
        var vm = Activated();
        Raise(1, 2, 3);
        vm.CodeText = "482913";
        _client.HoldDeviceId = Id(1);
        _client.HonoursCancellation = true;
        var run = vm.PairAllAsync();
        await _client.Reached;

        vm.Deactivate();
        await run;

        Assert.All(vm.Rows, r => Assert.Equal(SetupRowStatus.NotPaired, r.Status));
        Assert.Single(_client.Calls);
        Assert.Empty(_store.Members);
    }

    // A3: the singular on the success bar.

    [Fact]
    public async Task OnePc_SuccessTitle_UsesTheSingular()
    {
        var vm = Activated();
        Raise(1);
        vm.CodeText = "482913";

        await vm.PairAllAsync();

        Assert.Equal("1 PC is in this room.", vm.Banner?.Title);
        Assert.Equal(SetupBannerKind.Success, vm.Banner?.Kind);
    }

    [Fact]
    public async Task SeveralPcs_SuccessTitle_UsesThePlural()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";

        await vm.PairAllAsync();

        Assert.Equal("All 2 PCs are in this room.", vm.Banner?.Title);
    }

    // A4: discovery that cannot start.

    [Fact]
    public void WhenDiscoveryCannotStart_ShowsTheErrorBar_AndNoLookingState()
    {
        _discovery.CanStart = false;

        var vm = Activated();

        Assert.Equal(
            new SetupBanner(
                SetupBannerKind.Error,
                "Educast cannot look for student PCs.",
                "Another program on this PC is using network port 17844. Close any other copy of Educast, then open this screen again."),
            vm.DiscoveryError);
        Assert.True(vm.NothingFound);
        Assert.False(vm.ShowsLookingCard);
        Assert.False(vm.ShowsStillLooking);
    }

    [Fact]
    public async Task WhenDiscoveryCannotStart_RoomMembersAreStillListed_AndTheirActionsWork()
    {
        _discovery.CanStart = false;
        _store.Members.Add(new RoomMember(Id(1), "PC-01", "192.168.1.21", 17843, Today, Selected: true));

        var vm = Activated();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(SetupRowStatus.InRoom, row.Status);
        Assert.False(vm.ShowsStillLooking);
        Assert.True(row.CanEdit);
        vm.Rename(row, "Front left");
        Assert.Equal("Front left", Assert.Single(_store.Members).DisplayName);
        vm.Remove(row);
        Assert.Empty(_store.Members);
        Assert.Empty(vm.Rows);
        await Task.CompletedTask;
    }

    [Fact]
    public void WhenDiscoveryStarts_TheLookingStatesShow_AndThereIsNoError()
    {
        var vm = Activated();
        Assert.Null(vm.DiscoveryError);
        Assert.True(vm.ShowsLookingCard);
        Assert.False(vm.ShowsStillLooking);

        Raise(1);
        Assert.False(vm.ShowsLookingCard);
        Assert.True(vm.ShowsStillLooking);
    }

    // C: row actions and the code.

    [Fact]
    public void PairAndTryAgain_NeedAValidMainCode_EnterItsCodeDoesNot()
    {
        var vm = Activated();
        Raise(1);
        var row = Row(vm, 1);

        Assert.False(row.CanPairNow);
        Assert.True(row.CanAct);

        vm.CodeText = "48291";
        Assert.False(row.CanPairNow);

        vm.CodeText = "482 913";
        Assert.True(row.CanPairNow);
        Assert.True(row.CanAct);
    }

    [Fact]
    public async Task RenameAndRemove_AreDisabledWhilePairing()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";
        await vm.PairOneAsync(Row(vm, 1));
        Assert.True(Row(vm, 1).CanEdit);
        _client.HoldDeviceId = Id(2);

        var run = vm.PairAllAsync();
        await _client.Reached;
        Assert.False(Row(vm, 1).CanEdit);

        _client.Release();
        await run;
        Assert.True(Row(vm, 1).CanEdit);
    }

    [Fact]
    public async Task TheCode_IsCleared_WhenEveryPcIsPaired()
    {
        var vm = Activated();
        Raise(1, 2);
        vm.CodeText = "482913";

        await vm.PairAllAsync();

        Assert.False(vm.ShowCodeCard);
        Assert.Equal(string.Empty, vm.CodeText);
    }

    [Fact]
    public void AMemberWithoutAPin_IsDroppedFromTheRoom_AndAppearsNotPairedWhenSeen()
    {
        _store.Members.Add(new RoomMember(Id(1), "Front left", "192.168.1.21", 17843, Today, Selected: true));
        _store.Members.Add(new RoomMember(Id(2), "PC-02", "192.168.1.22", 17843, Today, Selected: true));
        _client.Trusted = id => id != Id(1);

        var vm = Activated();

        Assert.Equal([Id(2)], vm.Rows.Select(r => r.DeviceId));
        Assert.Equal([Id(2)], _store.Members.Select(m => m.DeviceId));

        Raise(1);
        Assert.Equal(SetupRowStatus.NotPaired, Row(vm, 1).Status);
        Assert.Equal("PC-01", Row(vm, 1).Name);
    }

    [Fact]
    public void AnUnreadableRoom_ShowsTheReadError_AndAnEmptyRoom()
    {
        _store.LoadThrows = new RoomStoreException("locked", new IOException());

        var vm = Activated();

        Assert.Empty(vm.Rows);
        Assert.Equal(
            new SetupBanner(
                SetupBannerKind.Error,
                "Educast could not read this room.",
                "Close any other copy of Educast, then start it again."),
            vm.StoreError);
        Assert.Equal(1, _discovery.Started);
    }

    [Fact]
    public void TheChecklist_IsOpenWhenNothingIsFound_AndClosesOnTheFirstPc()
    {
        var vm = Activated();
        Assert.True(vm.IsChecklistExpanded);

        Raise(1);

        Assert.False(vm.IsChecklistExpanded);
    }

    [Fact]
    public void TheChecklist_StaysAsThePersonSetIt()
    {
        var vm = Activated();
        vm.IsChecklistExpanded = false;
        vm.IsChecklistExpanded = true;

        Raise(1);

        Assert.True(vm.IsChecklistExpanded);
    }

    [Fact]
    public void TheChecklist_StartsClosed_WhenTheRoomHasMembers()
    {
        _store.Members.Add(new RoomMember(Id(1), "PC-01", "192.168.1.21", 17843, Today, Selected: true));

        var vm = Activated();

        Assert.False(vm.IsChecklistExpanded);
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
