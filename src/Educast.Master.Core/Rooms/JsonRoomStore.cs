using System.Text.Json;

namespace Educast.Master.Core.Rooms;

/// <summary>
/// The room as one JSON file (<c>%LocalAppData%\Educast\room.json</c> in the app).
/// </summary>
/// <remarks>
/// A missing file is an empty room. A file that is not JSON is set aside as
/// <c>room.json.corrupt</c> — kept, never deleted — and the room starts empty rather than the
/// console refusing to open. A save writes a complete temporary file beside the target and then
/// swaps it in, so a crash mid-write leaves the previous room intact.
/// </remarks>
public sealed class JsonRoomStore : IRoomStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonRoomStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<RoomMember> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var text = File.ReadAllText(_path);
        List<RoomMember?>? members;
        try
        {
            members = JsonSerializer.Deserialize<List<RoomMember?>>(text, Options);
        }
        catch (JsonException)
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
            return [];
        }

        return members is null
            ? []
            : members.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.DeviceId)).Select(m => m!).ToList();
    }

    public void Save(IReadOnlyList<RoomMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, members, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
