using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Wall;
using Educast.Protocol.Pairing;

namespace Educast.Master.Core.Setup;

/// <summary>
/// Everything the Set up devices page says and does. The page binds to it and calls into it;
/// nothing on the page decides anything.
/// </summary>
/// <remarks>
/// <b>The room code lives in <see cref="CodeText"/> and nowhere else.</b> It is normalised at the
/// moment of pairing, handed to the pairing client, and never written to the room store or
/// anything that persists. It is cleared as soon as every PC is in the room.
///
/// <b>Nothing escapes.</b> Every public member is called from a click handler or a navigation
/// event, so none of them throws for anything a disk, the network or a student PC can do: a
/// pairing that fails in an unexpected way reads <see cref="SetupRowStatus.CannotPairNow"/>, a
/// save that fails leaves the screen as it was and shows <see cref="StoreError"/>, a discovery
/// port that is taken shows <see cref="DiscoveryError"/>, and a batch never leaves a row
/// <see cref="SetupRowStatus.Waiting"/> or <see cref="SetupRowStatus.Pairing"/>.
///
/// <b>Threads.</b> Every public member is called on the UI thread. <see cref="IDiscovery.Seen"/>
/// arrives on any thread and is marshalled through <c>runOnUiThread</c>; the pairing client
/// returns on the caller's context, which in the app is the UI dispatcher.
///
/// <b>Bounded.</b> An announcement is untrusted input, so at most <see cref="MaxSeenPcs"/>
/// distinct device ids are kept; later new ones are ignored.
/// </remarks>
public sealed class SetupViewModel : INotifyPropertyChanged
{
    /// <summary>How many distinct PCs discovery may put on the screen.</summary>
    public const int MaxSeenPcs = 200;

    private const int MaxNameLength = 64;

    private static readonly StringComparer NameOrder = StringComparer.OrdinalIgnoreCase;

    private readonly IPairingClient _pairing;
    private readonly IDiscovery _discovery;
    private readonly IRoomStore _store;
    private readonly ConsoleHealth _health;
    private readonly IBroadcastController _broadcast;
    private readonly Func<DateOnly> _today;
    private readonly Action<Action> _runOnUiThread;

    private readonly List<RoomMember> _members = [];
    private readonly Dictionary<string, StudentPc> _seen = new(StringComparer.Ordinal);
    private readonly Action<StudentPc> _onSeen;
    private readonly PropertyChangedEventHandler _onHealthChanged;

    private string _codeText = string.Empty;
    private bool _active;
    private bool _isPairing;
    private bool _busy;
    private bool _cancelRequested;
    private int _batchTotal;
    private int _batchIndex;
    private CancellationTokenSource? _pairingCts;
    private bool _discoveryRunning;
    private SetupBanner? _discoveryError;
    private SetupBanner? _storeError;
    private bool _roomUnreadable;
    private bool _checklistExpanded;
    private bool _checklistTouched;
    private bool _settingChecklist;
    private bool _lastNothingFound = true;

    public SetupViewModel(
        IPairingClient pairing,
        IDiscovery discovery,
        IRoomStore store,
        ConsoleHealth health,
        IBroadcastController broadcast,
        Func<DateOnly> today,
        Action<Action> runOnUiThread)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(broadcast);
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(runOnUiThread);

        _pairing = pairing;
        _discovery = discovery;
        _store = store;
        _health = health;
        _broadcast = broadcast;
        _today = today;
        _runOnUiThread = runOnUiThread;

        _onSeen = pc => _runOnUiThread(() => OnSeen(pc));
        _onHealthChanged = (_, e) =>
        {
            if (e.PropertyName is null or nameof(ConsoleHealth.LocalFault))
            {
                RefreshActions();
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SetupRow> Rows { get; } = [];

    /// <summary>The "A PC is missing?" checklist, as the page shows it.</summary>
    public IReadOnlyList<ChecklistItem> Checklist => SetupChecklist.Items;

    /// <summary>The hint under the room code field while it holds something that is not a code.</summary>
    public const string CodeHintText = "The code is 6 digits";

    /// <summary>
    /// Whatever the room code field holds, raw — spaces, hyphens and letters included. Never
    /// stored anywhere else. Validity is <see cref="RoomCode.TryNormalize"/>, checked where the
    /// code is used, and what is sent is always the six digits.
    /// </summary>
    public string CodeText
    {
        get => _codeText;
        set
        {
            if (Set(ref _codeText, value ?? string.Empty))
            {
                Raise(nameof(ShowsCodeHint));
                RefreshActions();
            }
        }
    }

    /// <summary>True while the field is not empty and not a valid code.</summary>
    public bool ShowsCodeHint => _codeText.Length > 0 && !RoomCode.TryNormalize(_codeText, out _);

    public string CodeHint => CodeHintText;

    public bool CanPairAll =>
        RoomCode.TryNormalize(_codeText, out _)
        && Rows.Any(r => r.IsPairable)
        && ActionsAllowed;

    /// <summary>True while Pair all is running. A single row's Pair does not light this.</summary>
    public bool IsPairing => _isPairing;

    public string PairAllLabel
    {
        get
        {
            if (_isPairing)
            {
                return $"Pairing {_batchIndex} of {_batchTotal}";
            }

            var failed = Rows.Count(r => r.IsFailed);
            if (failed > 0)
            {
                return $"Try the {failed} again";
            }

            var toPair = Rows.Count(r => r.IsPairable);
            return toPair > 0 ? $"Pair all {toPair}" : "Pair all";
        }
    }

    public string CountText
    {
        get
        {
            var all = Rows.Count;
            if (all == 0)
            {
                return string.Empty;
            }

            var paired = Rows.Count(r => r.Status == SetupRowStatus.InRoom);
            if (_isPairing)
            {
                return $"{paired} paired so far";
            }

            if (paired == 0)
            {
                return $"{all} found, none paired yet";
            }

            return paired == all ? $"{all} paired" : $"{paired} paired, {all - paired} not";
        }
    }

    /// <summary>The result bar: a warning when some failed, success when all are in the room.</summary>
    public SetupBanner? Banner
    {
        get
        {
            if (_isPairing || Rows.Count == 0)
            {
                return null;
            }

            var all = Rows.Count;
            var paired = Rows.Count(r => r.Status == SetupRowStatus.InRoom);
            var failed = Rows.Count(r => r.IsFailed);

            if (failed > 0 && paired > 0)
            {
                var attention = failed == 1 ? "1 needs attention below." : $"{failed} need attention below.";
                return new SetupBanner(SetupBannerKind.Warning, $"{paired} of {all} PCs are in this room.", attention);
            }

            if (paired == all)
            {
                return new SetupBanner(
                    SetupBannerKind.Success,
                    all == 1 ? "1 PC is in this room." : $"All {all} PCs are in this room.",
                    "You only do this once. They reconnect by themselves from now on.");
            }

            return null;
        }
    }

    /// <summary>Set while this screen cannot look for PCs because the discovery port is taken.</summary>
    public SetupBanner? DiscoveryError => _discoveryError;

    /// <summary>Set when the room could not be read at start, or the last save failed.</summary>
    public SetupBanner? StoreError => _storeError;

    public bool ShowCodeCard => !(Rows.Count > 0 && Rows.All(r => r.Status == SetupRowStatus.InRoom));

    public bool NothingFound => Rows.Count == 0;

    public bool HasRows => Rows.Count > 0;

    /// <summary>The "Looking for student PCs" card: nothing found yet, and discovery is running.</summary>
    public bool ShowsLookingCard => NothingFound && _discoveryRunning;

    /// <summary>The "Still looking" indicator beside the count: rows exist, and discovery is running.</summary>
    public bool ShowsStillLooking => HasRows && _discoveryRunning;

    /// <summary>
    /// Whether the "A PC is missing?" checklist is open. Open by default while nothing is found,
    /// closed once something is — unless a person has opened or closed it by hand, in which case
    /// it stays as they set it.
    /// </summary>
    public bool IsChecklistExpanded
    {
        get => _checklistExpanded;
        set
        {
            if (_checklistExpanded == value)
            {
                return;
            }

            _checklistExpanded = value;
            if (!_settingChecklist)
            {
                _checklistTouched = true;
            }

            Raise();
        }
    }

    /// <summary>Page shown: load the room, start discovery. Never throws.</summary>
    /// <remarks>
    /// The discovery and health subscriptions live between <see cref="Activate"/> and
    /// <see cref="Deactivate"/>: both sources outlive any one page, so a view model that kept
    /// listening after its page had gone would be a leak and would keep updating a dead screen.
    /// </remarks>
    public void Activate()
    {
        if (!_active)
        {
            _active = true;
            _discovery.Seen += _onSeen;
            _health.PropertyChanged += _onHealthChanged;
        }

        Rows.Clear();
        _seen.Clear();
        _members.Clear();
        _checklistTouched = false;
        _roomUnreadable = false;
        SetStoreError(null);

        IReadOnlyList<RoomMember> loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (Exception)
        {
            loaded = [];
            _roomUnreadable = true;
            SetStoreError(SetupBanner.ReadError);
        }

        // A member this console no longer holds a pin for cannot be dialled, so it is not in the
        // room: it is dropped, and comes back as Not paired when it is next seen.
        var trusted = loaded.Where(m => _pairing.IsTrusted(m.DeviceId)).ToList();
        _members.AddRange(trusted);
        if (trusted.Count != loaded.Count)
        {
            TrySave();
        }

        foreach (var member in _members)
        {
            Insert(new SetupRow(member.DeviceId, member.DisplayName, member.LastAddress, SetupRowStatus.InRoom)
            {
                Port = member.Port,
            });
        }

        bool started;
        try
        {
            started = _discovery.Start();
        }
        catch (Exception)
        {
            started = false;
        }

        _discoveryRunning = started;
        Set(ref _discoveryError, started ? null : SetupBanner.DiscoveryUnavailable, nameof(DiscoveryError));

        _lastNothingFound = NothingFound;
        SetChecklist(NothingFound);
        RefreshAll();
    }

    /// <summary>Page left: stop discovery, cancel pairing.</summary>
    public void Deactivate()
    {
        if (_active)
        {
            _active = false;
            _discovery.Seen -= _onSeen;
            _health.PropertyChanged -= _onHealthChanged;
        }

        _discovery.Stop();
        _discoveryRunning = false;
        _cancelRequested = true;
        _pairingCts?.Cancel();
    }

    public async Task PairAllAsync()
    {
        if (!CanPairAll || !RoomCode.TryNormalize(_codeText, out var code))
        {
            return;
        }

        var batch = Rows.Where(r => r.IsPairable).ToList();
        foreach (var row in batch)
        {
            row.Status = SetupRowStatus.Waiting;
        }

        _batchTotal = batch.Count;
        _batchIndex = 0;
        _cancelRequested = false;
        _isPairing = true;
        using var cts = new CancellationTokenSource();
        _pairingCts = cts;
        RefreshAll();

        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var row = batch[i];
                if (_cancelRequested || cts.IsCancellationRequested)
                {
                    break;
                }

                _batchIndex = i + 1;
                Raise(nameof(PairAllLabel));
                await PairRowAsync(row, code, cts.Token).ConfigureAwait(true);
            }
        }
        finally
        {
            // Whatever happened, no row is left where it has no action.
            foreach (var row in batch)
            {
                if (row.Status is SetupRowStatus.Waiting or SetupRowStatus.Pairing)
                {
                    row.Status = SetupRowStatus.NotPaired;
                }
            }

            _pairingCts = null;
            _isPairing = false;
            RefreshAll();
        }
    }

    /// <summary>Stops after the PC being paired. Rows still waiting go back to Not paired.</summary>
    public void Cancel()
    {
        if (!_isPairing)
        {
            return;
        }

        _cancelRequested = true;
        foreach (var row in Rows.Where(r => r.Status == SetupRowStatus.Waiting).ToList())
        {
            row.Status = SetupRowStatus.NotPaired;
        }
    }

    /// <summary>
    /// Pairs one row with <paramref name="codeOverride"/> when given, otherwise with
    /// <see cref="CodeText"/>. An invalid code changes nothing and pairs nothing.
    /// </summary>
    public async Task PairOneAsync(SetupRow row, string? codeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!ActionsAllowed || !row.IsPairable
            || !RoomCode.TryNormalize(codeOverride ?? _codeText, out var code))
        {
            return;
        }

        _busy = true;
        using var cts = new CancellationTokenSource();
        _pairingCts = cts;
        RefreshActions();
        try
        {
            await PairRowAsync(row, code, cts.Token).ConfigureAwait(true);
        }
        finally
        {
            if (row.Status == SetupRowStatus.Pairing)
            {
                row.Status = SetupRowStatus.NotPaired;
            }

            _pairingCts = null;
            _busy = false;
            RefreshAll();
        }
    }

    /// <summary>Trims; ignores an empty name; cuts to 64 characters; saves. A failed save changes nothing.</summary>
    public void Rename(SetupRow row, string newName)
    {
        ArgumentNullException.ThrowIfNull(row);
        var name = (newName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd();
        }

        if (name == row.Name)
        {
            return;
        }

        var index = _members.FindIndex(m => m.DeviceId == row.DeviceId);
        if (index >= 0)
        {
            var before = _members[index];
            _members[index] = before with { DisplayName = name };
            if (!TrySave())
            {
                _members[index] = before;
                return;
            }
        }

        row.Name = name;
        Reposition(row);
    }

    /// <summary>
    /// Takes the PC out of the room, stops any broadcast to it, and forgets its certificate on this
    /// machine. The row stays, as Not paired, while the PC is still being seen; otherwise it goes.
    /// A failed save changes nothing.
    /// </summary>
    public void Remove(SetupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var index = _members.FindIndex(m => m.DeviceId == row.DeviceId);
        if (index >= 0)
        {
            var member = _members[index];
            _members.RemoveAt(index);
            if (!TrySave())
            {
                _members.Insert(index, member);
                return;
            }
        }

        _broadcast.StopSendingTo(row.DeviceId);

        try
        {
            _pairing.Unpair(row.DeviceId);
        }
        catch (Exception)
        {
            // The pin could not be written away. The room no longer lists the PC; a pin without a
            // member is harmless (pairing the same certificate again is idempotent) but the
            // person should know a save failed.
            SetStoreError(SetupBanner.SaveError);
        }

        if (_seen.TryGetValue(row.DeviceId, out var pc))
        {
            row.Status = SetupRowStatus.NotPaired;
            if (row.Name != pc.AnnouncedName)
            {
                row.Name = pc.AnnouncedName;
                Reposition(row);
            }
        }
        else
        {
            Rows.Remove(row);
        }

        RefreshAll();
    }

    private async Task PairRowAsync(SetupRow row, string code, CancellationToken ct)
    {
        row.Status = SetupRowStatus.Pairing;
        Raise(nameof(CountText));

        var pc = _seen.TryGetValue(row.DeviceId, out var seen)
            ? seen
            : new StudentPc(row.DeviceId, row.Name, row.Address, row.Port);

        PairResult result;
        try
        {
            result = await _pairing.PairAsync(pc, code, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            row.Status = SetupRowStatus.NotPaired;
            return;
        }
        catch (Exception)
        {
            // Not something the PC or the network did (those come back as results): this side
            // failed. The row says so; the batch goes on.
            row.Status = SetupRowStatus.CannotPairNow;
            return;
        }

        switch (result)
        {
            case PairResult.Paired:
                RecordPaired(row, pc);
                break;
            case PairResult.WrongCode:
                row.Status = SetupRowStatus.WrongCode;
                break;
            case PairResult.Unreachable:
                row.Status = SetupRowStatus.Unreachable;
                break;
            default:
                row.Status = SetupRowStatus.CannotPairNow;
                break;
        }

        Raise(nameof(CountText));
    }

    /// <summary>
    /// The transport has pinned the PC's certificate; the room must say so too, at once. If the
    /// room cannot be saved, the pin is undone so the PC is never trusted-but-not-in-the-room.
    /// </summary>
    private void RecordPaired(SetupRow row, StudentPc pc)
    {
        var member = new RoomMember(row.DeviceId, pc.AnnouncedName, pc.Address, pc.Port, _today(), Selected: true);
        var index = _members.FindIndex(m => m.DeviceId == row.DeviceId);
        var before = index >= 0 ? _members[index] : null;
        if (index >= 0)
        {
            _members[index] = member;
        }
        else
        {
            _members.Add(member);
        }

        if (TrySave())
        {
            row.Status = SetupRowStatus.InRoom;
            return;
        }

        if (before is not null)
        {
            _members[index] = before;
        }
        else
        {
            _members.Remove(member);
        }

        try
        {
            _pairing.Unpair(row.DeviceId);
        }
        catch (Exception)
        {
            // The save bar is already up; nothing more to say.
        }

        row.Status = SetupRowStatus.CannotPairNow;
    }

    /// <summary>Saves the room. False, with <see cref="StoreError"/> set, when it could not be.</summary>
    private bool TrySave()
    {
        if (_roomUnreadable)
        {
            // Writing a room over one that could not be read would lose it. The read bar stays
            // and says to restart.
            return false;
        }

        try
        {
            _store.Save(_members.ToList());
            SetStoreError(null);
            return true;
        }
        catch (Exception)
        {
            SetStoreError(SetupBanner.SaveError);
            return false;
        }
    }

    private void OnSeen(StudentPc pc)
    {
        if (!_active)
        {
            // Raised before Deactivate, delivered by the dispatcher after it.
            return;
        }

        var existing = Rows.FirstOrDefault(r => r.DeviceId == pc.DeviceId);
        if (!_seen.ContainsKey(pc.DeviceId) && existing is null && _seen.Count >= MaxSeenPcs)
        {
            return;
        }

        _seen[pc.DeviceId] = pc;

        if (existing is null)
        {
            Insert(new SetupRow(pc.DeviceId, pc.AnnouncedName, pc.Address, SetupRowStatus.NotPaired)
            {
                Port = pc.Port,
            });
            RefreshAll();
            return;
        }

        existing.Address = pc.Address;
        existing.Port = pc.Port;

        // A member keeps the name the teacher gave it; a PC not in the room is called what it calls itself.
        if (!_members.Any(m => m.DeviceId == pc.DeviceId) && existing.Name != pc.AnnouncedName)
        {
            existing.Name = pc.AnnouncedName;
            Reposition(existing);
        }
    }

    /// <summary>
    /// Whether any pairing action may be taken: no pairing running, this PC healthy, and the room
    /// readable — a pairing the room cannot record would only be undone (see <see cref="RecordPaired"/>),
    /// after the student PC had already accepted it.
    /// </summary>
    private bool ActionsAllowed => !_isPairing && !_busy && !_health.HasLocalFault && !_roomUnreadable;

    private void Insert(SetupRow row)
    {
        var index = 0;
        while (index < Rows.Count && NameOrder.Compare(Rows[index].Name, row.Name) <= 0)
        {
            index++;
        }

        Rows.Insert(index, row);
    }

    private void Reposition(SetupRow row)
    {
        var from = Rows.IndexOf(row);
        if (from < 0)
        {
            return;
        }

        var to = 0;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (i != from && NameOrder.Compare(Rows[i].Name, row.Name) <= 0)
            {
                to++;
            }
        }

        if (to != from)
        {
            Rows.Move(from, to);
        }
    }

    private void SetChecklist(bool expanded)
    {
        _settingChecklist = true;
        IsChecklistExpanded = expanded;
        _settingChecklist = false;
    }

    private void SetStoreError(SetupBanner? error) => Set(ref _storeError, error, nameof(StoreError));

    private void RefreshActions()
    {
        var allowed = ActionsAllowed;
        var codeValid = RoomCode.TryNormalize(_codeText, out _);
        var editable = !_isPairing && !_busy;
        foreach (var row in Rows)
        {
            row.CanAct = allowed;
            row.CanPairNow = allowed && codeValid;
            row.CanEdit = editable;
        }

        Raise(nameof(CanPairAll));
    }

    private void RefreshAll()
    {
        if (!ShowCodeCard && _codeText.Length > 0)
        {
            // Every PC is in the room; the code has done its work and is not kept.
            CodeText = string.Empty;
        }

        if (NothingFound != _lastNothingFound)
        {
            _lastNothingFound = NothingFound;
            if (!_checklistTouched)
            {
                SetChecklist(NothingFound);
            }
        }

        RefreshActions();
        Raise(nameof(IsPairing));
        Raise(nameof(PairAllLabel));
        Raise(nameof(CountText));
        Raise(nameof(Banner));
        Raise(nameof(ShowCodeCard));
        Raise(nameof(NothingFound));
        Raise(nameof(HasRows));
        Raise(nameof(ShowsLookingCard));
        Raise(nameof(ShowsStillLooking));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
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
