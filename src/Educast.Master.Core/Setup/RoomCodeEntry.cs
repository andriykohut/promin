using Educast.Protocol.Pairing;

namespace Educast.Master.Core.Setup;

/// <summary>
/// What the room code field contains after an input (design doc 6.2: digits only, exactly six).
/// </summary>
/// <remarks>
/// The field's control reports what it would contain after a keystroke or a paste; this decides
/// what it does contain. Up to six ASCII digits are taken as they are, so typing and deleting
/// work character by character. A paste that <see cref="RoomCode.TryNormalize"/> accepts
/// ("482 913", "482-913") becomes the six digits. Anything else — a letter, a seventh digit, a
/// full-width digit, junk — is refused and the field keeps what it had.
/// </remarks>
public static class RoomCodeEntry
{
    public static string Accept(string current, string proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        if (proposed.Length <= RoomCode.Length && proposed.All(c => c is >= '0' and <= '9'))
        {
            return proposed;
        }

        return RoomCode.TryNormalize(proposed, out var code) ? code : current;
    }
}
