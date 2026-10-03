namespace Educast.Protocol.Pairing;

/// <summary>The room code an admin sets at install and types once on the teacher PC.</summary>
public static class RoomCode
{
    public const int Length = 6;

    public static bool IsValid(ReadOnlySpan<char> code)
    {
        if (code.Length != Length)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Accepts what a person types or pastes: digits, optionally grouped with spaces or
    /// hyphens ("482 913", "482-913"). Anything else is refused rather than repaired.
    /// </summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = string.Empty;
        if (input is null)
        {
            return false;
        }

        Span<char> digits = stackalloc char[Length];
        var count = 0;
        foreach (var c in input)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            if (c is < '0' or > '9' || count == Length)
            {
                return false;
            }

            digits[count++] = c;
        }

        if (count != Length)
        {
            return false;
        }

        code = new string(digits);
        return true;
    }
}
