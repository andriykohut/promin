using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Promin.Master.Core.Setup;

/// <summary>
/// One agent PC on the setup screen. The page binds to this and nothing else decides what a
/// row says: the status wording and which action a row offers are derived here from
/// <see cref="Status"/>.
/// </summary>
public sealed class SetupRow : INotifyPropertyChanged
{
    private string _name;
    private string _address;
    private SetupRowStatus _status;
    private bool _canAct = true;
    private bool _canPairNow;
    private bool _canEdit = true;

    public SetupRow(string deviceId, string name, string address, SetupRowStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(address);

        DeviceId = deviceId;
        _name = name;
        _address = address;
        _status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DeviceId { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Address
    {
        get => _address;
        set => Set(ref _address, value);
    }

    public SetupRowStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value))
            {
                return;
            }

            Raise(nameof(StatusText));
            Raise(nameof(StatusDetail));
            Raise(nameof(IsFailed));
            Raise(nameof(IsPairing));
            Raise(nameof(ShowsNeutralDot));
            Raise(nameof(ShowsSuccessDot));
            Raise(nameof(ShowsPlainStatus));
            Raise(nameof(ShowsPair));
            Raise(nameof(ShowsEnterCode));
            Raise(nameof(ShowsTryAgain));
            Raise(nameof(ShowsMenu));
        }
    }

    /// <summary>
    /// Whether a pairing action may be taken on this row now. False while a pairing is running
    /// anywhere on the screen and while this PC has a fault of its own. Gates Enter its code.
    /// </summary>
    public bool CanAct
    {
        get => _canAct;
        internal set => Set(ref _canAct, value);
    }

    /// <summary><see cref="CanAct"/>, and the main field holds a valid code. Gates Pair and Try again.</summary>
    public bool CanPairNow
    {
        get => _canPairNow;
        internal set => Set(ref _canPairNow, value);
    }

    /// <summary>Whether Rename and Remove may be taken: no pairing is running anywhere on the screen.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        internal set => Set(ref _canEdit, value);
    }

    /// <summary>The port the PC was last announced on. Not shown.</summary>
    internal int Port { get; set; }

    public string StatusText => _status switch
    {
        SetupRowStatus.NotPaired => Strings.Row_NotPaired,
        SetupRowStatus.Waiting => Strings.Row_Waiting,
        SetupRowStatus.Pairing => Strings.Row_Pairing,
        SetupRowStatus.InRoom => Strings.Row_InRoom,
        SetupRowStatus.WrongCode => Strings.Row_WrongCode,
        SetupRowStatus.Unreachable => Strings.Row_Unreachable,
        SetupRowStatus.CannotPairNow => Strings.Row_CannotPairNow,
        _ => string.Empty,
    };

    /// <summary>The second line under a failure; null for every other status.</summary>
    public string? StatusDetail => _status switch
    {
        SetupRowStatus.WrongCode => Strings.Row_WrongCodeDetail,
        SetupRowStatus.Unreachable => Strings.Row_UnreachableDetail,
        SetupRowStatus.CannotPairNow => Strings.Row_CannotPairNowDetail,
        _ => null,
    };

    public bool IsFailed => _status is SetupRowStatus.WrongCode or SetupRowStatus.Unreachable or SetupRowStatus.CannotPairNow;

    public bool IsPairing => _status == SetupRowStatus.Pairing;

    public bool ShowsNeutralDot => _status is SetupRowStatus.NotPaired or SetupRowStatus.Waiting;

    public bool ShowsSuccessDot => _status == SetupRowStatus.InRoom;

    /// <summary>The status reads in the ordinary text colour: in the room, or being paired.</summary>
    public bool ShowsPlainStatus => _status is SetupRowStatus.InRoom or SetupRowStatus.Pairing;

    public bool ShowsPair => _status == SetupRowStatus.NotPaired;

    public bool ShowsEnterCode => _status == SetupRowStatus.WrongCode;

    public bool ShowsTryAgain => _status is SetupRowStatus.Unreachable or SetupRowStatus.CannotPairNow;

    public bool ShowsMenu => _status == SetupRowStatus.InRoom;

    /// <summary>True for a row that Pair all would pair: not in the room, and not already on its way.</summary>
    internal bool IsPairable => _status == SetupRowStatus.NotPaired || IsFailed;

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
