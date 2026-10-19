using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>Which received items are never opened for the person, because Windows itself warns before opening them.</summary>
internal static class DangerousTypes
{
    /// <summary>
    /// False for a folder and for a name without an extension; otherwise what Windows says of the extension.
    /// Trailing dots and spaces are ignored, as Windows ignores them, and a file name holding a colon (an
    /// alternate data stream) is always dangerous.
    /// </summary>
    public static bool IsDangerous(string path)
    {
        if (Directory.Exists(path))
        {
            return false;
        }

        // A stream is opened by the type of what it holds, not by the extension in the name; fail closed.
        if (Path.GetFileName(path).Contains(':'))
        {
            return true;
        }

        // Win32 drops trailing dots and spaces from a path, so "tool.exe." and "tool.exe " open tool.exe.
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        return extension.Length > 0 && AssocIsDangerous(extension);
    }
}
