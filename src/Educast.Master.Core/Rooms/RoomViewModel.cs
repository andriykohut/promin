using System.ComponentModel;

namespace Educast.Master.Core.Rooms;

/// <summary>
/// What the room page says for now: whether the room is empty, and how many PCs it has. Task 13
/// replaces the count with the wall.
/// </summary>
public sealed class RoomViewModel : INotifyPropertyChanged
{
    private readonly IRoomStore _store;
    private int _count;

    public RoomViewModel(IRoomStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEmpty => _count == 0;

    public bool HasMembers => _count > 0;

    public string MemberCountText => _count switch
    {
        0 => string.Empty,
        1 => "1 PC in this room.",
        _ => $"{_count} PCs in this room.",
    };

    /// <summary>Page shown: read the room.</summary>
    public void Activate()
    {
        _count = _store.Load().Count;
        Raise(nameof(IsEmpty));
        Raise(nameof(HasMembers));
        Raise(nameof(MemberCountText));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
