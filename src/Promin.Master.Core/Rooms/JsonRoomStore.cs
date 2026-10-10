using System.Text.Json;
using Promin.Protocol.Transport;

namespace Promin.Master.Core.Rooms;

/// <summary>
/// The room as one JSON file (<c>%LocalAppData%\Promin\room.json</c> in the app).
/// </summary>
/// <remarks>
/// A missing file is an empty room. A file that is not JSON is set aside as
/// <c>room.json.corrupt</c> — kept, never deleted — and the room starts empty rather than the
/// console refusing to open. A file that cannot be read or written at all (locked, access
/// denied, disk full) is a <see cref="RoomStoreException"/>, which the screen reports; it is
/// never treated as an empty room, because that would silently lose the room. A member with no
/// device id, name or address is skipped. A save writes a complete temporary file beside the
/// target and then swaps it in, so a crash mid-write leaves the previous room intact.
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

        try
        {
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
                : members.Where(IsWhole).Select(m => m!).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RoomStoreException($"The room file {_path} could not be read.", e);
        }
    }

    public void Save(IReadOnlyList<RoomMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        try
        {
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(members, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RoomStoreException($"The room file {_path} could not be written.", e);
        }
    }

    private static bool IsWhole(RoomMember? member) =>
        member is not null
        && !string.IsNullOrWhiteSpace(member.DeviceId)
        && !string.IsNullOrWhiteSpace(member.DisplayName)
        && !string.IsNullOrWhiteSpace(member.LastAddress);
}
