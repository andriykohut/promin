using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Educast.Master.Core;

/// <summary>
/// What is wrong with this PC itself, as opposed to a student PC. Set by the adapters at start,
/// read by both pages: while a fault is set, one error bar shows at the top of the window and
/// every Pair action is disabled, so a broken console never blames a student PC.
/// </summary>
public sealed class ConsoleHealth : INotifyPropertyChanged
{
    /// <summary>The title of the error bar for a certificate this PC cannot use.</summary>
    public const string CertificateFaultTitle = "This PC's certificate cannot be used.";

    /// <summary>The text of the error bar for a certificate this PC cannot use.</summary>
    public const string CertificateFaultMessage =
        "Educast cannot pair or broadcast until it is repaired. Restart Educast; if this stays, remove the " +
        @"folder %LocalAppData%\Educast\identity and pair the room again.";

    private string? _localFault;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The text of the fault, or null when this PC is healthy.</summary>
    public string? LocalFault
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
        }
    }

    public bool HasLocalFault => _localFault is not null;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
