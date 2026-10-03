using Educast.Master.Core.Rooms;
using Educast.Master.Core.Tests.TestDoubles;

namespace Educast.Master.Core.Tests.Rooms;

public sealed class RoomViewModelTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    private readonly InMemoryRoomStore _store = new();

    private static RoomMember Member(int n) =>
        new($"device-{n:00}", $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, Today, Selected: true);

    [Fact]
    public void AnEmptyRoom_IsEmpty()
    {
        var vm = new RoomViewModel(_store);

        vm.Activate();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasMembers);
        Assert.Equal(string.Empty, vm.MemberCountText);
    }

    [Fact]
    public void OneMember_UsesTheSingular()
    {
        _store.Members.Add(Member(1));
        var vm = new RoomViewModel(_store);

        vm.Activate();

        Assert.False(vm.IsEmpty);
        Assert.Equal("1 PC in this room.", vm.MemberCountText);
    }

    [Fact]
    public void SeveralMembers_AreCounted()
    {
        _store.Members.AddRange([Member(1), Member(2), Member(3)]);
        var vm = new RoomViewModel(_store);

        vm.Activate();

        Assert.True(vm.HasMembers);
        Assert.Equal("3 PCs in this room.", vm.MemberCountText);
    }

    [Fact]
    public void Activate_ReadsTheRoomAgain()
    {
        var vm = new RoomViewModel(_store);
        vm.Activate();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _store.Members.Add(Member(1));
        vm.Activate();

        Assert.False(vm.IsEmpty);
        Assert.Contains(nameof(RoomViewModel.IsEmpty), raised);
        Assert.Contains(nameof(RoomViewModel.MemberCountText), raised);
    }
}
