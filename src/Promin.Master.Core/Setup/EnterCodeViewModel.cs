using System.ComponentModel;
using Promin.Protocol.Pairing;

namespace Promin.Master.Core.Setup;

/// <summary>
/// The "Enter its code" dialog: a stock password box whose content is checked on the action,
/// not as it is typed. <see cref="CodeText"/> is the raw content; <see cref="Code"/> is the six
/// digits the action uses, or null while the content is not a code.
/// </summary>
public sealed class EnterCodeViewModel : INotifyPropertyChanged
{
    public const string HintText = SetupViewModel.CodeHintText;

    private string _codeText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CodeText
    {
        get => _codeText;
        set
        {
            var text = value ?? string.Empty;
            if (text == _codeText)
            {
                return;
            }

            _codeText = text;
            Raise(nameof(CodeText));
            Raise(nameof(ShowsHint));
            Raise(nameof(CanPair));
            Raise(nameof(Code));
        }
    }

    /// <summary>True while the field is not empty and not a valid code.</summary>
    public bool ShowsHint => _codeText.Length > 0 && !RoomCode.TryNormalize(_codeText, out _);

    public bool CanPair => RoomCode.TryNormalize(_codeText, out _);

    /// <summary>The six digits, or null while the content is not a code.</summary>
    public string? Code => RoomCode.TryNormalize(_codeText, out var code) ? code : null;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
