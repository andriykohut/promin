using System.ComponentModel;
using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Rooms;

/// <summary>
/// What the room page says for now: whether the room is empty, how many PCs it has, and whether
/// the room could be read at all. Task 13 replaces the count with the wall.
/// </summary>
public sealed class RoomViewModel : INotifyPropertyChanged
{
    private readonly IRoomStore _store;
    private int _count;
    private SetupBanner? _loadError;

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

    /// <summary>Set when the room file exists but could not be read; the room then reads as empty.</summary>
    public SetupBanner? LoadError => _loadError;

    public bool HasLoadError => _loadError is not null;

    public string LoadErrorTitle => _loadError?.Title ?? string.Empty;

    public string LoadErrorMessage => _loadError?.Message ?? string.Empty;

    /// <summary>Page shown: read the room. Never throws.</summary>
    public void Activate()
    {
        try
        {
            _count = _store.Load().Count;
            _loadError = null;
        }
        catch (Exception)
        {
            _count = 0;
            _loadError = SetupBanner.ReadError;
        }

        Raise(nameof(IsEmpty));
        Raise(nameof(HasMembers));
        Raise(nameof(MemberCountText));
        Raise(nameof(LoadError));
        Raise(nameof(HasLoadError));
        Raise(nameof(LoadErrorTitle));
        Raise(nameof(LoadErrorMessage));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
