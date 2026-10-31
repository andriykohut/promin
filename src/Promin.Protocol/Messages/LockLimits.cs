namespace Promin.Protocol.Messages;

/// <summary>What a lock message may say: one short line, the same on the console PC that types it and the agent PC that shows it.</summary>
public static class LockLimits
{
    public const int MaxMessageLength = 100;

    /// <summary>
    /// The text as one line of at most <see cref="MaxMessageLength"/> characters: each control character becomes a
    /// space, the ends are trimmed, and a cut never splits a surrogate pair. Returns an empty string for blank text.
    /// </summary>
    public static string Clean(string text)
    {
        var line = string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        }).Trim();

        if (line.Length > MaxMessageLength)
        {
            var end = char.IsHighSurrogate(line[MaxMessageLength - 1]) ? MaxMessageLength - 1 : MaxMessageLength;
            line = line[..end].TrimEnd();
        }

        return line;
    }
}
