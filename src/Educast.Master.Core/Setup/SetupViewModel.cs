using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Educast.Master.Core.Rooms;
using Educast.Protocol.Pairing;

namespace Educast.Master.Core.Setup;

/// <summary>
/// Everything the Set up devices page says and does. The page binds to it and calls into it;
/// nothing on the page decides anything.
/// </summary>
/// <remarks>
/// <b>The room code lives in <see cref="CodeText"/> and nowhere else.</b> It is normalised at the
/// moment of pairing, handed to the pairing client, and never written to the room store or
/// anything that persists.
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

    public SetupViewModel(
        IPairingClient pairing,
        IDiscovery discovery,
        IRoomStore store,
        ConsoleHealth health,
        Func<DateOnly> today,
        Action<Action> runOnUiThread)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(runOnUiThread);

        _pairing = pairing;
        _discovery = discovery;
        _store = store;
        _health = health;
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

    /// <summary>Whatever is typed in the room code field. Never stored anywhere else.</summary>
    public string CodeText
    {
        get => _codeText;
        set
        {
            if (Set(ref _codeText, value ?? string.Empty))
            {
                Raise(nameof(CanPairAll));
            }
        }
    }

    public bool CanPairAll =>
        RoomCode.TryNormalize(_codeText, out _)
        && Rows.Any(r => r.IsPairable)
        && !_isPairing
        && !_busy
        && !_health.HasLocalFault;

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
                    $"All {all} PCs are in this room.",
                    "You only do this once. They reconnect by themselves from now on.");
            }

            return null;
        }
    }

    public bool ShowCodeCard => !(Rows.Count > 0 && Rows.All(r => r.Status == SetupRowStatus.InRoom));

    public bool NothingFound => Rows.Count == 0;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Page shown: load the room, start discovery.</summary>
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
        _members.AddRange(_store.Load());
        foreach (var member in _members)
        {
            Insert(new SetupRow(member.DeviceId, member.DisplayName, member.LastAddress, SetupRowStatus.InRoom)
            {
                Port = member.Port,
            });
        }

        RefreshAll();
        _discovery.Start();
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
                    ReturnToNotPaired(batch.Skip(i));
                    break;
                }

                _batchIndex = i + 1;
                Raise(nameof(PairAllLabel));
                await PairRowAsync(row, code, cts.Token).ConfigureAwait(true);
            }
        }
        finally
        {
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
        ReturnToNotPaired(Rows.Where(r => r.Status == SetupRowStatus.Waiting).ToList());
    }

    /// <summary>
    /// Pairs one row with <paramref name="codeOverride"/> when given, otherwise with
    /// <see cref="CodeText"/>. An invalid code changes nothing and pairs nothing.
    /// </summary>
    public async Task PairOneAsync(SetupRow row, string? codeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_isPairing || _busy || _health.HasLocalFault || !row.IsPairable
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
            _pairingCts = null;
            _busy = false;
            RefreshAll();
        }
    }

    /// <summary>Trims; ignores an empty name; cuts to 64 characters; saves.</summary>
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

        row.Name = name;
        Reposition(row);

        var index = _members.FindIndex(m => m.DeviceId == row.DeviceId);
        if (index >= 0)
        {
            _members[index] = _members[index] with { DisplayName = name };
            _store.Save(_members.ToList());
        }
    }

    /// <summary>
    /// Forgets the PC's certificate on this machine and takes it out of the room. The row stays,
    /// as Not paired, while the PC is still being seen; otherwise it goes.
    /// </summary>
    public void Remove(SetupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        _pairing.Unpair(row.DeviceId);
        if (_members.RemoveAll(m => m.DeviceId == row.DeviceId) > 0)
        {
            _store.Save(_members.ToList());
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

        switch (result)
        {
            case PairResult.Paired:
                // The transport has pinned the PC's certificate; the room must say so too, at
                // once, whatever happens next.
                var member = new RoomMember(
                    row.DeviceId, pc.AnnouncedName, pc.Address, pc.Port, _today(), Selected: true);
                var index = _members.FindIndex(m => m.DeviceId == row.DeviceId);
                if (index >= 0)
                {
                    _members[index] = member;
                }
                else
                {
                    _members.Add(member);
                }

                _store.Save(_members.ToList());
                row.Status = SetupRowStatus.InRoom;
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
                CanAct = ActionsAllowed,
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

    private void ReturnToNotPaired(IEnumerable<SetupRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Status == SetupRowStatus.Waiting)
            {
                row.Status = SetupRowStatus.NotPaired;
            }
        }
    }

    private bool ActionsAllowed => !_isPairing && !_busy && !_health.HasLocalFault;

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

    private void RefreshActions()
    {
        var allowed = ActionsAllowed;
        foreach (var row in Rows)
        {
            row.CanAct = allowed;
        }

        Raise(nameof(CanPairAll));
    }

    private void RefreshAll()
    {
        RefreshActions();
        Raise(nameof(IsPairing));
        Raise(nameof(PairAllLabel));
        Raise(nameof(CountText));
        Raise(nameof(Banner));
        Raise(nameof(ShowCodeCard));
        Raise(nameof(NothingFound));
        Raise(nameof(HasRows));
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

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
