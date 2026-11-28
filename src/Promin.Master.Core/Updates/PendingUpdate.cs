using System.Text.Json;
using System.Text.Json.Serialization;
using Promin.Protocol.Transport;

namespace Promin.Master.Core.Updates;

/// <summary>
/// What the console noted before running its own setup: the version it is updating to, and the connected PCs it is to
/// update once it opens again on that version. The console's setup relaunches it with <c>--continue-update</c>, which
/// reads and deletes this file.
/// </summary>
public sealed record PendingUpdate(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("deviceIds")] IReadOnlyList<string> DeviceIds)
{
    /// <summary>The file at <paramref name="path"/>; null when it is missing, unreadable, or lacks either field.</summary>
    public static PendingUpdate? Read(string path)
    {
        try
        {
            var read = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllBytes(path));
            return read is { Version: not null, DeviceIds: not null } ? read : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the file atomically, creating its folder if needed.</summary>
    public void Write(string path) => AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>Deletes the file if it is there. Never throws.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read again at the next relaunch, and ignored unless its version is the console's own.
        }
    }
}
