using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>Which received items are never opened for the person, because Windows itself warns before opening them.</summary>
internal static class DangerousTypes
{
    /// <summary>False for a folder and for a name without an extension; otherwise what Windows says of the extension.</summary>
    public static bool IsDangerous(string path)
    {
        if (Directory.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Length > 0 && AssocIsDangerous(extension);
    }
}
