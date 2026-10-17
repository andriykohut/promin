using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Wall;

/// <summary>
/// One agent PC on the wall. The page binds to this and nothing else decides what a tile says:
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
    private string _selectBoxName = string.Empty;
    private string _detailsButtonName = string.Empty;
    private string _tileName = string.Empty;
    private TileScreen _screen;
    private byte[]? _picture;

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

    /// <summary>What the screen area shows over the state's background.</summary>
    public TileScreen Screen
    {
        get => _screen;
        private set => Set(ref _screen, value);
    }

    /// <summary>The newest picture of the PC's screen, a JPEG, while <see cref="Screen"/> is <see cref="TileScreen.Picture"/>; otherwise null.</summary>
    public byte[]? Picture
    {
        get => _picture;
        private set => Set(ref _picture, value);
    }

    public string StatusText
    {
        get => _statusText;
        internal set => Set(ref _statusText, value);
    }

    /// <summary>A picture from the PC; an empty one says its screen is not available.</summary>
    internal void ShowPicture(byte[] jpeg)
    {
        Picture = jpeg.Length == 0 ? null : jpeg;
        Screen = jpeg.Length == 0 ? TileScreen.NotAvailable : TileScreen.Picture;
    }

    internal void ClearPicture()
    {
        Picture = null;
        Screen = TileScreen.Glyph;
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

    /// <summary>What a screen reader says for the checkbox: <c>Select {name}</c>.</summary>
    public string SelectBoxName
    {
        get => _selectBoxName;
        internal set => Set(ref _selectBoxName, value);
    }

    /// <summary>What a screen reader says for the details button: <c>Details for {name}</c>.</summary>
    public string DetailsButtonName
    {
        get => _detailsButtonName;
        internal set => Set(ref _detailsButtonName, value);
    }

    /// <summary>What a screen reader says for the tile: <c>{name}, {status}</c>.</summary>
    public string TileName
    {
        get => _tileName;
        internal set => Set(ref _tileName, value);
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
