using System.Text.Json;
using Promin.Protocol.Transport;

namespace Promin.Agent;

/// <summary>
/// The lock on an agent PC, kept as <c>{"message":"..."}</c> in one file so that it outlives the agent and the
/// PC's restarts. The file's existence is the lock.
/// </summary>
internal sealed class LockFile(string path, Action<string> log)
{
    private sealed record Stored(string? Message);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The lock message (possibly empty) when the PC is locked, or null when there is no file or it cannot be
    /// read. An unreadable file means not locked, with a line in the log: a stuck PC is worse than a free one.
    /// </summary>
    public string? Read()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json)?.Message is { } message)
            {
                return message;
            }

            log($"{path} exists but holds no lock message; the PC is not locked.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log($"{path} exists but could not be read ({e.Message}); the PC is not locked.");
        }

        return null;
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public void Write(string message) =>
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new Stored(message), Json));

    /// <summary>Removes the file; nothing happens when there is none.</summary>
    /// <exception cref="IOException">The file could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be deleted.</exception>
    public void Clear() => File.Delete(path);
}
