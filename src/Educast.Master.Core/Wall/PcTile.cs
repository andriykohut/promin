using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Educast.Master.Core.Rooms;

namespace Educast.Master.Core.Wall;

/// <summary>
/// One student PC on the wall. The page binds to this and nothing else decides what a tile says:
/// <see cref="RoomWallViewModel"/> sets every value here on each tick.
/// </summary>
public sealed class PcTile : INotifyPropertyChanged
{
    private string _name;
    private string _address;
    private PcTileState _state;
    private string _statusText = string.Empty;
    private string _detailSentence = string.Empty;
    private bool _isSelected;
    private bool _canSelect;
    private bool _isOnAir;
    private int _framesDropped;
    private int _framesOffered;
    private string _receivingForText = string.Empty;

    public PcTile(RoomMember member, TimeSpan dropWindow)
    {
        ArgumentNullException.ThrowIfNull(member);
        DeviceId = member.DeviceId;
        _name = member.DisplayName;
        _address = member.LastAddress;
        PairedOnText = member.PairedOn.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        Drops = new RecentDrops(dropWindow);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DeviceId { get; }

    public string Name
    {
        get => _name;
        internal set => Set(ref _name, value);
    }

    public PcTileState State
    {
        get => _state;
        internal set => Set(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        internal set => Set(ref _statusText, value);
    }

    /// <summary>One sentence on the state when it is not healthy; empty otherwise.</summary>
    public string DetailSentence
    {
        get => _detailSentence;
        internal set => Set(ref _detailSentence, value);
    }

    /// <summary>The remembered flag while <see cref="CanSelect"/>; otherwise false.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>Off air and online.</summary>
    public bool CanSelect
    {
        get => _canSelect;
        internal set => Set(ref _canSelect, value);
    }

    /// <summary>Whether a broadcast is running; the details panel shows its frame counts and clock only then.</summary>
    public bool IsOnAir
    {
        get => _isOnAir;
        internal set => Set(ref _isOnAir, value);
    }

    public string Address
    {
        get => _address;
        internal set => Set(ref _address, value);
    }

    public string PairedOnText { get; }

    /// <summary>Frames this broadcast queued for the PC that never arrived.</summary>
    public int FramesDropped
    {
        get => _framesDropped;
        internal set => Set(ref _framesDropped, value);
    }

    public int FramesOffered
    {
        get => _framesOffered;
        internal set => Set(ref _framesOffered, value);
    }

    /// <summary>How long the broadcast has been running, as <see cref="WallText.Elapsed"/> writes it; empty off air.</summary>
    public string ReceivingForText
    {
        get => _receivingForText;
        internal set => Set(ref _receivingForText, value);
    }

    internal RecentDrops Drops { get; }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
