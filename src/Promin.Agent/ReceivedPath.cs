namespace Promin.Agent;

/// <summary>The rule for paths a send carries. They come from the network, so only plain names inside the folder pass.</summary>
internal static class ReceivedPath
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    // Windows' documented reserved device names, including the superscript digits it also treats as devices.
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. "0123456789¹²³".SelectMany(digit => new[] { $"COM{digit}", $"LPT{digit}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="relative"/> is a plain relative path, with <c>/</c> between names, that stays
    /// inside <paramref name="root"/> once joined; <paramref name="full"/> is then the joined full path. Refused:
    /// an empty name, <c>.</c> or <c>..</c>, a name ending in a dot or a space, a character not allowed in a file
    /// name (which takes in <c>\</c> and <c>:</c>), and a reserved device name with or without an extension.
    /// </summary>
    public static bool TryJoin(string root, string relative, out string full)
    {
        full = "";
        foreach (var name in relative.Split('/'))
        {
            if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(InvalidChars) >= 0
                || ReservedNames.Contains(name.Split('.')[0].TrimEnd(' ')))
            {
                return false;
            }
        }

        var fullRoot = Path.GetFullPath(root);
        var joined = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', '\\')));
        if (!joined.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + '\\', StringComparison.Ordinal))
        {
            return false;
        }

        full = joined;
        return true;
    }
}
