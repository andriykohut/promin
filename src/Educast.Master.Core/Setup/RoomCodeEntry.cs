using Educast.Protocol.Pairing;

namespace Educast.Master.Core.Setup;

/// <summary>What the room code field holds after an edit, and where its caret goes.</summary>
public readonly record struct CodeEdit(string Text, int Caret);

/// <summary>
/// What happens to the room code field for each input (design doc 6.2: digits only, exactly
/// six). The field's control reports an edit before it takes it — the current code, where the
/// caret or selection is, and what would go in — and these rules say what the field holds
/// afterwards, or refuse the edit so that nothing changes at all.
/// </summary>
/// <remarks>
/// <b>Typing</b> (<see cref="Edit"/>): digits go in where the caret is, replacing any selection,
/// as long as the result is at most six; a deletion is always allowed; anything that is not an
/// ASCII digit — a letter, a symbol, a space, a full-width digit, the mask character — is
/// refused as a whole, selection included.
///
/// <b>Pasting</b> (<see cref="Paste"/>): text that <see cref="RoomCode.TryNormalize"/> accepts
/// ("482913", "482 913", "482-913", with or without surrounding spaces) replaces the whole
/// field; plain ASCII digits that fit go in where the caret is, as typing would; anything else
/// is refused.
/// </remarks>
public static class RoomCodeEntry
{
    /// <summary>The character a masked field shows for each digit: the same as a Windows password field.</summary>
    public const char MaskChar = '●';

    public static string Mask(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new string(MaskChar, code.Length);
    }

    /// <summary>
    /// A keystroke, an overtype, a deletion, or whatever an input method inserts.
    /// <paramref name="inserted"/> is empty for a deletion. Returns null to refuse.
    /// </summary>
    public static CodeEdit? Edit(string current, int selectionStart, int selectionLength, string inserted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(inserted);

        if (!IsDigits(inserted))
        {
            return null;
        }

        var (start, length) = Clamp(current, selectionStart, selectionLength);
        if (current.Length - length + inserted.Length > RoomCode.Length)
        {
            return null;
        }

        return new CodeEdit(current[..start] + inserted + current[(start + length)..], start + inserted.Length);
    }

    /// <summary>Clipboard text pasted with the caret or selection where it is. Returns null to refuse.</summary>
    public static CodeEdit? Paste(string current, int selectionStart, int selectionLength, string clipboard)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(clipboard);

        if (RoomCode.TryNormalize(clipboard, out var code))
        {
            return new CodeEdit(code, code.Length);
        }

        return clipboard.Length > 0 && IsDigits(clipboard)
            ? Edit(current, selectionStart, selectionLength, clipboard)
            : null;
    }

    private static bool IsDigits(string text) => text.All(c => c is >= '0' and <= '9');

    private static (int Start, int Length) Clamp(string current, int start, int length)
    {
        start = Math.Clamp(start, 0, current.Length);
        length = Math.Clamp(length, 0, current.Length - start);
        return (start, length);
    }
}
