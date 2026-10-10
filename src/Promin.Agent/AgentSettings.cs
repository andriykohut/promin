using System.Text.Json;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Agent;

/// <summary>
/// The room code on an agent PC: <c>{"roomCode":"482913"}</c> in a file every user of the PC can read. That is
/// accepted: knowing the code is of no use against a PC that already has a console PC pinned.
/// </summary>
internal sealed class AgentSettings(string path)
{
    private sealed record Stored(string? RoomCode);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The stored code, or null: no file, a file that cannot be read or is not JSON, or a value that is not
    /// exactly six digits.
    /// </summary>
    public string? Load() => Lookup().Code;

    /// <summary>
    /// The same, and when there is no usable code but there is a file, what is wrong with it. No file at all is
    /// not a problem: it is a PC whose room code has not been set.
    /// </summary>
    public RoomCodeLookup Lookup()
    {
        if (!File.Exists(path))
        {
            return new RoomCodeLookup(null);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            return stored?.RoomCode is { } code && RoomCode.IsValid(code)
                ? new RoomCodeLookup(code)
                : new RoomCodeLookup(null, $"{path} exists but does not hold a six-digit room code.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new RoomCodeLookup(null, $"{path} exists but could not be read ({e.Message}).");
        }
    }

    /// <summary>Writes <paramref name="roomCode"/> (six digits) to the file, replacing it whole, and creates the folder if needed.</summary>
    /// <exception cref="ArgumentException">Not six digits.</exception>
    /// <exception cref="IOException">The file or folder could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or folder could not be written.</exception>
    public void Save(string roomCode)
    {
        if (!RoomCode.IsValid(roomCode))
        {
            throw new ArgumentException("The room code must be exactly six digits.", nameof(roomCode));
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Stored(roomCode), Json));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
