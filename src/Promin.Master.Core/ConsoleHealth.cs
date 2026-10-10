using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Promin.Master.Core;

/// <summary>Something wrong with this PC itself that stops Promin pairing or broadcasting.</summary>
public sealed record LocalFault(string Title, string Message)
{
    /// <summary>This PC's own certificate cannot complete a TLS handshake.</summary>
    public static LocalFault UnusableCertificate { get; } = new(
        "This PC's certificate cannot be used.",
        "Promin cannot pair or broadcast until it is repaired. Restart Promin; if this stays, remove the " +
        @"folder %LocalAppData%\Promin\identity and pair the room again.");

    /// <summary>This Windows has no QUIC, so no secure connection can be made at all.</summary>
    public static LocalFault NoSecureTransport { get; } = new(
        "Promin needs Windows 11.",
        "This version of Windows cannot make the secure connections Promin uses.");
}

/// <summary>
/// What is wrong with this PC itself, as opposed to a student PC. Set by the adapters at start,
/// read by both pages: while a fault is set, one error bar shows at the top of the window and
/// every Pair action is disabled, so a broken console never blames a student PC.
/// </summary>
public sealed class ConsoleHealth : INotifyPropertyChanged
{
    private LocalFault? _localFault;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The fault, or null when this PC is healthy.</summary>
    public LocalFault? LocalFault
    {
        get => _localFault;
        set
        {
            if (_localFault == value)
            {
                return;
            }

            _localFault = value;
            Raise();
            Raise(nameof(HasLocalFault));
            Raise(nameof(LocalFaultTitle));
            Raise(nameof(LocalFaultMessage));
        }
    }

    public bool HasLocalFault => _localFault is not null;

    public string LocalFaultTitle => _localFault?.Title ?? string.Empty;

    public string LocalFaultMessage => _localFault?.Message ?? string.Empty;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
