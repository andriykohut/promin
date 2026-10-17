using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;
using Promin.Protocol.Video;

namespace Promin.Master.Core.Wall;

/// <summary>
/// Everything the room wall says and does. The page binds to it and calls into it; nothing on
/// the page decides anything.
/// </summary>
/// <remarks>
/// <b>Nothing escapes.</b> Every public member is called from a click handler, a timer or a
/// navigation event, so none of them throws for anything a disk, the network or an agent PC can
/// do: a start that fails reads <see cref="WallMessage.BroadcastStopped"/>, a save that fails
/// leaves the room as it was and reads <see cref="WallMessage.RoomNotSaved"/>.
///
/// <b>Threads.</b> Every public member is called on the UI thread. <see cref="IDiscovery.Seen"/>,
/// <see cref="IBroadcastController.ClientLost"/> and <see cref="IBroadcastController.BroadcastFailed"/>
/// arrive on any thread and are marshalled through <c>runOnUiThread</c>;
/// <see cref="IRoomConnections.PictureArrived"/> and <see cref="IRoomConnections.Ended"/> too.
///
/// <b>State.</b> Presence and the broadcast's counters are read on <see cref="Tick"/>; an
/// announcement only records when a PC was seen. The subscriptions are made on the first
/// <see cref="Activate"/> and kept: the wall is the everyday screen and its page lives as long
/// as the window, so presence stays current while Set up devices is open.
/// </remarks>
public sealed class RoomWallViewModel : INotifyPropertyChanged
{
    /// <summary>A PC is online while it was seen within this time: three announcements.</summary>
    public static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(6);

    /// <summary>A PC is dropping frames while it dropped one within this time.</summary>
    public static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(10);

    /// <summary>On air, a PC that could not be joined is not tried again sooner than this after the attempt started.</summary>
    private static readonly TimeSpan RejoinInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long <see cref="WallMessage.IsBack"/> shows.</summary>
    private static readonly TimeSpan BackMessageTime = TimeSpan.FromSeconds(6);

    private readonly IRoomStore _store;
    private readonly IDiscovery _discovery;
    private readonly IBroadcastController _broadcast;
    private readonly IRoomConnections _connections;
    private readonly IPairingClient _pairing;
    private readonly IWallSettings _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<Action> _runOnUiThread;

    private readonly PresenceTracker _presence = new(PresenceTimeout);
    private readonly List<RoomMember> _members = [];
    private readonly HashSet<string> _sendingTo = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stoppedByPresenter = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejoining = new(StringComparer.Ordinal);

    // PCs reported lost while a start was still connecting: not receiving when it returns, whatever it says.
    private readonly HashSet<string> _lostWhileStarting = new(StringComparer.Ordinal);

    // Where each member last announced itself from: where it is dialled next. Anyone on the network can send an
    // announcement, so this is kept in memory only; the room file gets an address once a connection worked there.
    private readonly Dictionary<string, (string Address, int Port)> _announcedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _rejoinTriedAt = new(StringComparer.Ordinal);

    // The newest picture each PC sent on its current connection, whatever its tile shows at the moment: on air, the first
    // picture of a rejoined connection arrives while the tile still reads Reconnecting, and the agent sends the next one
    // only when its screen changes.
    private readonly Dictionary<string, byte[]> _pictures = new(StringComparer.Ordinal);

    // Cancelled when the broadcast ends, so a PC still connecting is let go instead of being left waiting for a picture.
    private CancellationTokenSource _rejoins = new();

    // The PC the "is back" message is about; the message shows only while that PC is being sent to.
    private string? _backId;
    private WallMessage? _backMessage;
    private DateTimeOffset _backUntil;

    private bool _active;
    private bool _starting;
    private bool _onAir;
    private bool _onAirShown;
    private DateTimeOffset _startedAt;
    private bool _roomUnreadable;
    private bool _saveFailed;
    private bool _broadcastStopped;

    private QualityPreset _selectedPreset;
    private Sharpness _sharpness;

    private IReadOnlyList<WallPill> _pills = [];
    private string _barLine1 = string.Empty;
    private string _barLine2 = string.Empty;
    private string _elapsedText = string.Empty;
    private bool _canStart;
    private bool _canChooseQuality;
    private WallMessage? _message;
    private IReadOnlyList<SharpnessChoice> _sharpnessChoices = [];
    private SharpnessChoice _selectedSharpness = null!;
    private string _qualityButtonText = string.Empty;
    private string _qualityPickerName = string.Empty;
    private string _networkLoadText = string.Empty;

    public RoomWallViewModel(
        IRoomStore store,
        IDiscovery discovery,
        IBroadcastController broadcast,
        IRoomConnections connections,
        IPairingClient pairing,
        IWallSettings settings,
        Func<DateTimeOffset> now,
        Action<Action> runOnUiThread)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(broadcast);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(runOnUiThread);

        _store = store;
        _discovery = discovery;
        _broadcast = broadcast;
        _connections = connections;
        _pairing = pairing;
        _settings = settings;
        _now = now;
        _runOnUiThread = runOnUiThread;

        _selectedPreset = QualityPreset.All.FirstOrDefault(p => p.Label == settings.QualityLabel) ?? QualityPreset.Default;
        _sharpness = Enum.TryParse<Sharpness>(settings.SharpnessName, out var remembered) && Enum.IsDefined(remembered)
            ? remembered
            : VideoBitrate.DefaultSharpness;

        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PcTile> Tiles { get; } = [];

    public IReadOnlyList<WallPill> Pills => _pills;

    public bool IsOnAir => _onAir;

    public bool IsEmptyRoom => Tiles.Count == 0;

    public bool CanStart => _canStart;

    public string BarLine1 => _barLine1;

    public string BarLine2 => _barLine2;

    /// <summary>The broadcast's running time; empty off air.</summary>
    public string ElapsedText => _elapsedText;

    public IReadOnlyList<QualityPreset> Presets => QualityPreset.All;

    /// <summary>Ignored on air and while there is no hardware encoder.</summary>
    public QualityPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!_canChooseQuality || value is null || value == _selectedPreset)
            {
                return;
            }

            _selectedPreset = value;
            _settings.QualityLabel = value.Label;
            Raise();
            Refresh();
        }
    }

    public bool CanChooseQuality => _canChooseQuality;

    /// <summary>Light, Standard, Sharp, each with the rate it gives for <see cref="SelectedPreset"/> on this PC.</summary>
    public IReadOnlyList<SharpnessChoice> SharpnessChoices => _sharpnessChoices;

    /// <summary>Ignored on air.</summary>
    public SharpnessChoice SelectedSharpness
    {
        get => _selectedSharpness;
        set
        {
            if (_onAir || _starting || value is null || value.Value == _sharpness)
            {
                return;
            }

            _sharpness = value.Value;
            _settings.SharpnessName = value.Value.ToString();
            Refresh();
        }
    }

    /// <summary>The picker's button: quality and rate together.</summary>
    public string QualityButtonText => _qualityButtonText;

    /// <summary>What a screen reader says for the picker: <c>Quality, {QualityButtonText}</c>.</summary>
    public string QualityPickerName => _qualityPickerName;

    /// <summary>What the broadcast costs the network for the PCs selected and online.</summary>
    public string NetworkLoadText => _networkLoadText;

    public WallMessage? Message => _message;

    // The bar binds these, never Message itself: a binding through a null object is never re-evaluated.

    public bool IsMessageOpen => _message is not null;

    public string MessageTitle => _message?.Title ?? string.Empty;

    public string MessageText => _message?.Text ?? string.Empty;

    public string MessageActionLabel => _message?.ActionLabel ?? string.Empty;

    public bool HasMessageAction => _message?.ActionLabel is not null;

    /// <summary><see cref="WallMessageKind.Info"/> when there is no message.</summary>
    public WallMessageKind MessageKind => _message?.Kind ?? WallMessageKind.Info;

    /// <summary>Page shown: start listening, read the room. Never throws.</summary>
    public void Activate()
    {
        if (!_active)
        {
            _active = true;
            _discovery.Seen += OnSeenAnyThread;
            _broadcast.ClientLost += OnClientLostAnyThread;
            _broadcast.BroadcastFailed += OnBroadcastFailedAnyThread;
            _connections.PictureArrived += OnPictureAnyThread;
            _connections.Ended += OnEndedAnyThread;
        }

        try
        {
            _discovery.Start();
        }
        catch (Exception)
        {
            // Every PC then reads Offline; Set up devices says why.
        }

        LoadRoom();
        Refresh();
    }

    public async Task StartAsync()
    {
        if (!_canStart)
        {
            return;
        }

        var now = _now();
        var targets = _members.Where(m => m.Selected && _presence.IsOnline(m.DeviceId, now)).Select(AsDialled).ToList();

        _starting = true;
        _lostWhileStarting.Clear();
        _broadcastStopped = false;
        Refresh();

        IReadOnlyCollection<string> unreachable;
        try
        {
            unreachable = await _broadcast
                .StartAsync(targets, _selectedPreset, _sharpness, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            // No capture device, or the encoder could not be built: the wall stays off air and says so.
            _starting = false;
            _broadcastStopped = true;
            Refresh();
            return;
        }

        _starting = false;
        _onAir = true;
        _startedAt = _now();
        _sendingTo.Clear();
        _stoppedByPresenter.Clear();
        foreach (var target in targets)
        {
            if (unreachable.Contains(target.DeviceId) || _lostWhileStarting.Contains(target.DeviceId))
            {
                continue;
            }

            // Removed from the room while the start was connecting, here or on Set up devices (which takes
            // its pin away before the wall reads the room again): it is not sent the picture.
            if (MemberOf(target.DeviceId) is null || !_pairing.IsTrusted(target.DeviceId))
            {
                _broadcast.StopSendingTo(target.DeviceId);
                continue;
            }

            _sendingTo.Add(target.DeviceId);
        }

        RememberAddresses(StoredRoom(), targets.Where(t => _sendingTo.Contains(t.DeviceId)));

        foreach (var tile in Tiles)
        {
            tile.Drops.Reset();
            tile.FramesDropped = 0;
            tile.FramesOffered = 0;
        }

        Refresh();
    }

    public void Stop()
    {
        if (!_onAir)
        {
            return;
        }

        _broadcast.Stop();
        EndBroadcast();
        Refresh();
    }

    public void ToggleSelected(PcTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (!tile.CanSelect || MemberOf(tile.DeviceId) is not { } member)
        {
            return;
        }

        SetFlags([member], !member.Selected);
    }

    /// <summary>Selects every online PC; offline ones keep their flag.</summary>
    public void SelectAll() => SetFlags(OnlineMembers(), selected: true);

    /// <summary>Clears every online PC; offline ones keep their flag.</summary>
    public void ClearSelection() => SetFlags(OnlineMembers(), selected: false);

    /// <summary>
    /// Takes the PC out of this broadcast, receiving or still reconnecting, and it is not rejoined;
    /// it stays paired and keeps its flag for the next one.
    /// </summary>
    public void StopSendingTo(PcTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (!_onAir || !_stoppedByPresenter.Add(tile.DeviceId))
        {
            return;
        }

        if (_sendingTo.Remove(tile.DeviceId))
        {
            _broadcast.StopSendingTo(tile.DeviceId);
        }

        Refresh();
    }

    /// <summary>Trims; ignores an empty name; cuts to 64 characters; saves. A failed save changes nothing.</summary>
    public void Rename(PcTile tile, string newName)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (RoomMember.CleanName(newName) is not { } name)
        {
            return;
        }

        var index = _members.FindIndex(m => m.DeviceId == tile.DeviceId);
        if (index < 0 || name == _members[index].DisplayName)
        {
            return;
        }

        var before = _members[index];
        _members[index] = before with { DisplayName = name };
        if (TrySave())
        {
            tile.Name = name;
        }
        else
        {
            _members[index] = before;
        }

        Refresh();
    }

    /// <summary>
    /// Takes the PC out of the room and forgets its certificate on this machine. On air it stops
    /// sending to it first. A failed save changes nothing.
    /// </summary>
    public void Remove(PcTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var index = _members.FindIndex(m => m.DeviceId == tile.DeviceId);
        if (index < 0)
        {
            return;
        }

        var member = _members[index];
        _members.RemoveAt(index);
        if (!TrySave())
        {
            _members.Insert(index, member);
            Refresh();
            return;
        }

        if (_sendingTo.Remove(tile.DeviceId))
        {
            _broadcast.StopSendingTo(tile.DeviceId);
        }

        _stoppedByPresenter.Remove(tile.DeviceId);
        _pictures.Remove(tile.DeviceId);

        try
        {
            _pairing.Unpair(tile.DeviceId);
        }
        catch (Exception)
        {
            // The pin could not be written away. The room no longer lists the PC; a pin without a
            // member is harmless, but the person should know a save failed.
            _saveFailed = true;
        }

        _connections.Drop(tile.DeviceId);
        Tiles.Remove(tile);
        Raise(nameof(IsEmptyRoom));
        Refresh();
    }

    /// <summary>
    /// Once a second from the page's timer: keeps a connection wanted for every online PC; on air, starts joining each PC the broadcast wants and
    /// is not sending to (lost, unreachable or offline at the start) that is online now; then reads
    /// presence and the broadcast's counters and recomputes every state and text.
    /// </summary>
    public void Tick()
    {
        var now = _now();

        // Every online PC has a standing connection: its screen pictures come on it, and a broadcast uses it.
        foreach (var member in _members.Where(m => _presence.IsOnline(m.DeviceId, now)))
        {
            _connections.Want(AsDialled(member));
        }

        foreach (var member in _members.Where(m => IsWanted(m) && !_sendingTo.Contains(m.DeviceId)).ToList())
        {
            if (_presence.IsOnline(member.DeviceId, now)
                && !_rejoining.Contains(member.DeviceId)
                && !(_rejoinTriedAt.TryGetValue(member.DeviceId, out var triedAt) && now - triedAt < RejoinInterval))
            {
                _ = RejoinAsync(AsDialled(member), now);
            }
        }

        Refresh();
    }

    /// <summary>One attempt to bring a PC into the running broadcast. Never throws.</summary>
    private async Task RejoinAsync(RoomMember member, DateTimeOffset now)
    {
        var id = member.DeviceId;
        _rejoining.Add(id);
        _rejoinTriedAt[id] = now;

        bool joined;
        try
        {
            joined = await _broadcast.AddAsync(member, _rejoins.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Not reached, or the broadcast ended meanwhile: like any failed attempt, tried again later.
            joined = false;
        }

        _rejoining.Remove(id);
        if (!joined)
        {
            return;
        }

        // The broadcast ended, or the PC was stopped or removed, while it was connecting: it is not sent the
        // picture. Removed includes removed on Set up devices, which takes its pin away before the wall reads
        // the room again.
        if (!IsWanted(MemberOf(id)) || !_pairing.IsTrusted(id))
        {
            _broadcast.StopSendingTo(id);
            return;
        }

        _sendingTo.Add(id);
        RememberAddresses(StoredRoom(), [member]);
        var tile = TileOf(id);
        tile.Drops.Reset();
        _backId = id;
        _backMessage = WallMessage.IsBack(tile.Name);
        _backUntil = _now() + BackMessageTime;
        Refresh();
    }

    private void LoadRoom()
    {
        IReadOnlyList<RoomMember> loaded;
        try
        {
            loaded = _store.Load();
            _roomUnreadable = false;
        }
        catch (Exception)
        {
            // A room that could not be read is not a room that was emptied: the wall keeps what it
            // has (and keeps sending), and says so.
            _roomUnreadable = true;
            return;
        }

        _members.Clear();
        _members.AddRange(loaded);

        // The tiles follow the room, which Set up devices may have changed meanwhile: a PC removed
        // there while on air is no longer sent to, and existing tiles keep their drop history.
        var existing = Tiles.ToDictionary(t => t.DeviceId, StringComparer.Ordinal);
        foreach (var gone in existing.Keys.Where(id => !_members.Any(m => m.DeviceId == id)))
        {
            if (_sendingTo.Remove(gone))
            {
                _broadcast.StopSendingTo(gone);
            }

            _stoppedByPresenter.Remove(gone);
            _pictures.Remove(gone);
            _connections.Drop(gone);
        }

        Tiles.Clear();
        foreach (var member in _members)
        {
            if (existing.TryGetValue(member.DeviceId, out var tile))
            {
                tile.Name = member.DisplayName;
                tile.Address = member.LastAddress;
            }
            else
            {
                tile = new PcTile(member, DropWindow);
            }

            Tiles.Add(tile);
        }

        Raise(nameof(IsEmptyRoom));
    }

    private void OnSeenAnyThread(AgentPc pc) => _runOnUiThread(() => OnSeen(pc));

    private void OnPictureAnyThread(string deviceId, byte[] picture) => _runOnUiThread(() => OnPicture(deviceId, picture));

    private void OnEndedAnyThread(string deviceId) => _runOnUiThread(() => OnEnded(deviceId));

    private void OnEnded(string deviceId)
    {
        _pictures.Remove(deviceId);
        Tiles.FirstOrDefault(t => t.DeviceId == deviceId)?.ClearPicture();
    }

    /// <summary>
    /// A PC that is offline or reconnecting shows the hatch, so its picture is only remembered then;
    /// <see cref="Refresh"/> shows it once the PC is back.
    /// </summary>
    private void OnPicture(string deviceId, byte[] picture)
    {
        if (Tiles.FirstOrDefault(t => t.DeviceId == deviceId) is not { } tile)
        {
            return;
        }

        _pictures[deviceId] = picture;
        if (tile.State is not (PcTileState.Offline or PcTileState.Reconnecting))
        {
            tile.ShowPicture(picture);
        }
    }

    private void OnClientLostAnyThread(string deviceId) => _runOnUiThread(() => OnClientLost(deviceId));

    private void OnBroadcastFailedAnyThread() => _runOnUiThread(OnBroadcastFailed);

    private void OnSeen(AgentPc pc)
    {
        // Nothing is kept for a PC that is not in this room, and nothing is saved here: see _announcedAt.
        if (MemberOf(pc.DeviceId) is null)
        {
            return;
        }

        _presence.Seen(pc.DeviceId, _now());
        _announcedAt[pc.DeviceId] = (pc.Address, pc.Port);
    }

    /// <summary>The member with the address it last announced itself from, which is where the broadcast dials it.</summary>
    private RoomMember AsDialled(RoomMember member) =>
        _announcedAt.TryGetValue(member.DeviceId, out var at)
            ? member with { LastAddress = at.Address, Port = at.Port }
            : member;

    /// <summary>
    /// The room as the store holds it now: Set up devices may have changed it since the wall read it.
    /// Null when it cannot be read.
    /// </summary>
    private List<RoomMember>? StoredRoom()
    {
        try
        {
            return _store.Load().ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A connection to each of these PCs, pinned to it, just worked at the address it carries: that
    /// is the address the room remembers. Written into <paramref name="stored"/>, the room as the
    /// store holds it, and nothing else in it is touched: the wall's own copy may be older. One save,
    /// only if an address differs; a PC no longer in the stored room is not written back.
    /// </summary>
    private void RememberAddresses(List<RoomMember>? stored, IEnumerable<RoomMember> reached)
    {
        if (stored is null)
        {
            return;
        }

        var changed = new List<RoomMember>();
        foreach (var pc in reached)
        {
            var index = stored.FindIndex(m => m.DeviceId == pc.DeviceId);
            if (index >= 0 && (stored[index].LastAddress != pc.LastAddress || stored[index].Port != pc.Port))
            {
                stored[index] = stored[index] with { LastAddress = pc.LastAddress, Port = pc.Port };
                changed.Add(pc);
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        try
        {
            _store.Save(stored);
            _saveFailed = false;
        }
        catch (Exception)
        {
            _saveFailed = true;
            return;
        }

        // The wall's copy follows for these PCs, so what it shows and what it saves later agree with the file.
        foreach (var pc in changed)
        {
            var index = _members.FindIndex(m => m.DeviceId == pc.DeviceId);
            if (index >= 0)
            {
                _members[index] = _members[index] with { LastAddress = pc.LastAddress, Port = pc.Port };
                TileOf(pc.DeviceId).Address = pc.LastAddress;
            }
        }
    }

    private void OnClientLost(string deviceId)
    {
        if (_starting)
        {
            _lostWhileStarting.Add(deviceId);
        }
        else if (_onAir && _sendingTo.Remove(deviceId))
        {
            Refresh();
        }
    }

    private void OnBroadcastFailed()
    {
        if (!_onAir)
        {
            return;
        }

        EndBroadcast();
        _broadcastStopped = true;
        Refresh();
    }

    private void EndBroadcast()
    {
        _onAir = false;
        _sendingTo.Clear();
        _stoppedByPresenter.Clear();
        _backId = null;

        // The next broadcast's source first; the callbacks of the ended one run on the pool, and nothing they throw comes back here.
        var ended = _rejoins;
        _rejoins = new CancellationTokenSource();
        _ = ended.CancelAsync();
    }

    private List<RoomMember> OnlineMembers()
    {
        var now = _now();
        return _members.Where(m => _presence.IsOnline(m.DeviceId, now)).ToList();
    }

    /// <summary>Sets the remembered flag of each member and saves once. Off air only; a failed save changes nothing.</summary>
    private void SetFlags(IReadOnlyList<RoomMember> members, bool selected)
    {
        if (_onAir || _starting)
        {
            return;
        }

        var changed = members.Where(m => m.Selected != selected).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var member in changed)
        {
            _members[_members.IndexOf(member)] = member with { Selected = selected };
        }

        if (!TrySave())
        {
            foreach (var member in changed)
            {
                _members[_members.FindIndex(m => m.DeviceId == member.DeviceId)] = member;
            }
        }

        Refresh();
    }

    /// <summary>Saves the room. False, with <see cref="WallMessage.RoomNotSaved"/> showing, when it could not be.</summary>
    private bool TrySave()
    {
        if (_roomUnreadable)
        {
            // Writing a room over one that could not be read would lose it.
            return false;
        }

        try
        {
            _store.Save(_members.ToList());
            _saveFailed = false;
            return true;
        }
        catch (Exception)
        {
            _saveFailed = true;
            return false;
        }
    }

    private RoomMember? MemberOf(string deviceId) => _members.FirstOrDefault(m => m.DeviceId == deviceId);

    private PcTile TileOf(string deviceId) => Tiles.First(t => t.DeviceId == deviceId);

    /// <summary>Whether the running broadcast should be sending to this PC: selected, and not stopped by the presenter.</summary>
    private bool IsWanted(RoomMember? member) =>
        _onAir && member is { Selected: true } && !_stoppedByPresenter.Contains(member.DeviceId);

    private PcTileState StateOf(RoomMember member, PcTile tile, bool online, DateTimeOffset now)
    {
        if (_onAir)
        {
            if (_sendingTo.Contains(member.DeviceId))
            {
                return tile.Drops.IsDropping(now) ? PcTileState.DroppingFrames : PcTileState.Receiving;
            }

            if (IsWanted(member))
            {
                return PcTileState.Reconnecting;
            }
        }

        return online ? PcTileState.Ready : PcTileState.Offline;
    }

    private void Refresh()
    {
        var now = _now();
        var hardware = _broadcast.HardwareEncoderAvailable;
        if (!hardware && _selectedPreset != QualityPreset.SoftwareFallback)
        {
            _selectedPreset = QualityPreset.SoftwareFallback;
            Raise(nameof(SelectedPreset));
        }

        var selectable = !_onAir && !_starting;
        var ready = 0;
        var offline = 0;
        var receiving = 0;
        var selectedAndOnline = 0;
        var anyOnline = false;
        var offlineNames = new List<string>();
        var droppingNames = new List<string>();
        var reconnectingNames = new List<string>();

        foreach (var tile in Tiles)
        {
            var member = MemberOf(tile.DeviceId)!;
            var online = _presence.IsOnline(tile.DeviceId, now);
            anyOnline |= online;

            if (_onAir && _sendingTo.Contains(tile.DeviceId))
            {
                var stats = _broadcast.StatsFor(tile.DeviceId);
                tile.Drops.Observe(stats.FramesDropped, now);
                tile.FramesDropped = stats.FramesDropped;
                tile.FramesOffered = stats.FramesOffered;
            }

            var state = StateOf(member, tile, online, now);
            tile.State = state;
            if (state is PcTileState.Offline or PcTileState.Reconnecting)
            {
                tile.ClearPicture();
            }
            else if (tile.Screen == TileScreen.Glyph && _pictures.TryGetValue(tile.DeviceId, out var remembered))
            {
                tile.ShowPicture(remembered);
            }

            tile.StatusText = WallText.Status(state, tile.FramesDropped);
            tile.DetailSentence = WallText.DetailSentence(state);
            tile.CanSelect = selectable && online;
            tile.IsSelected = tile.CanSelect && member.Selected;
            tile.IsOnAir = _onAir;
            tile.ReceivingForText = _onAir ? WallText.Elapsed(now - _startedAt) : string.Empty;
            tile.SelectBoxName = WallText.SelectBoxName(tile.Name);
            tile.DetailsButtonName = WallText.DetailsButtonName(tile.Name);
            tile.TileName = WallText.TileName(tile.Name, tile.StatusText);

            if (member.Selected && online)
            {
                selectedAndOnline++;
            }

            switch (state)
            {
                case PcTileState.Ready:
                    ready++;
                    break;
                case PcTileState.Offline:
                    offline++;
                    offlineNames.Add(tile.Name);
                    break;
                case PcTileState.Receiving:
                    receiving++;
                    break;
                case PcTileState.DroppingFrames:
                    droppingNames.Add(tile.Name);
                    break;
                case PcTileState.Reconnecting:
                    reconnectingNames.Add(tile.Name);
                    break;
            }
        }

        var empty = Tiles.Count == 0;
        Set(ref _pills, _onAir
            ? WallText.PillsOnAir(receiving, droppingNames.Count, reconnectingNames.Count)
            : WallText.PillsOffAir(ready, offline), nameof(Pills), SamePills);
        Set(ref _barLine1, empty ? WallText.EmptyRoomLine1 : _onAir ? WallText.OnAirLine1 : WallText.SelectedLine(selectedAndOnline, Tiles.Count), nameof(BarLine1));
        Set(ref _barLine2, empty
            ? WallText.EmptyRoomLine2
            : _onAir ? WallText.OnAirLine(receiving, droppingNames, reconnectingNames) : WallText.OfflineLine(offlineNames), nameof(BarLine2));
        Set(ref _elapsedText, _onAir ? WallText.Elapsed(now - _startedAt) : string.Empty, nameof(ElapsedText));
        Set(ref _canStart, selectable && !_roomUnreadable && selectedAndOnline > 0, nameof(CanStart));
        Set(ref _canChooseQuality, selectable && hardware, nameof(CanChooseQuality));
        Set(ref _onAirShown, _onAir, nameof(IsOnAir));

        if (Set(ref _message, _roomUnreadable ? WallMessage.RoomUnreadable
            : _broadcastStopped ? WallMessage.BroadcastStopped
            : _saveFailed ? WallMessage.RoomNotSaved
            : !empty && !anyOnline ? WallMessage.NoPcReachable
            : _backId is not null && _sendingTo.Contains(_backId) && now < _backUntil ? _backMessage
            : !hardware ? WallMessage.SoftwareEncoding
            : null, nameof(Message)))
        {
            Raise(nameof(IsMessageOpen));
            Raise(nameof(MessageTitle));
            Raise(nameof(MessageText));
            Raise(nameof(MessageActionLabel));
            Raise(nameof(HasMessageAction));
            Raise(nameof(MessageKind));
        }

        // The rates are the real ones for this PC: its display, scaled to the chosen ceiling.
        var display = _broadcast.DisplaySize;
        var encodeSize = display.Width > 0 && display.Height > 0
            ? FrameSizing.EncodeSize(display, _selectedPreset.Ceiling)
            : _selectedPreset.Ceiling;
        var culture = CultureInfo.CurrentCulture;
        var choices = new[] { Sharpness.Light, Sharpness.Standard, Sharpness.Sharp }
            .Select(s => new SharpnessChoice(s, WallText.SharpnessText(s, VideoBitrate.BitsPerSecond(encodeSize, _selectedPreset.Fps, s), culture)))
            .ToList();
        var rate = VideoBitrate.BitsPerSecond(encodeSize, _selectedPreset.Fps, _sharpness);
        Set(ref _sharpnessChoices, choices, nameof(SharpnessChoices), (a, b) => a.SequenceEqual(b));
        Set(ref _selectedSharpness, choices.Single(c => c.Value == _sharpness), nameof(SelectedSharpness));
        Set(ref _qualityButtonText, WallText.QualityButton(_selectedPreset.Label, rate, culture), nameof(QualityButtonText));
        Set(ref _qualityPickerName, WallText.QualityPickerName(_qualityButtonText), nameof(QualityPickerName));
        Set(ref _networkLoadText, WallText.NetworkLoad(rate, selectedAndOnline, culture), nameof(NetworkLoadText));
    }

    private static bool SamePills(IReadOnlyList<WallPill> a, IReadOnlyList<WallPill> b) => a.SequenceEqual(b);

    private bool Set<T>(ref T field, T value, string name, Func<T, T, bool>? same = null)
    {
        if (same is null ? EqualityComparer<T>.Default.Equals(field, value) : same(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
