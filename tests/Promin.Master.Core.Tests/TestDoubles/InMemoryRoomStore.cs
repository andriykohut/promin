using System.Text.Json;
using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Tests.TestDoubles;

/// <summary>
/// A room store that keeps the members in memory and, like the real one, serialises every save
/// — so a test can look at the bytes that would have reached the disk. It can also fail the way
/// a disk does.
/// </summary>
public sealed class InMemoryRoomStore : IRoomStore
{
    public List<RoomMember> Members { get; set; } = [];

    /// <summary>Everything every <see cref="Save"/> would have written, concatenated.</summary>
    public string Serialised { get; private set; } = string.Empty;

    public int Saves { get; private set; }

    /// <summary>Thrown by <see cref="Save"/> while set; the members are left as they were.</summary>
    public Exception? SaveThrows { get; set; }

    /// <summary>Thrown by <see cref="Load"/> while set.</summary>
    public Exception? LoadThrows { get; set; }

    public IReadOnlyList<RoomMember> Load()
    {
        if (LoadThrows is not null)
        {
            throw LoadThrows;
        }

        return Members.ToList();
    }

    public void Save(IReadOnlyList<RoomMember> members)
    {
        if (SaveThrows is not null)
        {
            throw SaveThrows;
        }

        Members = members.ToList();
        Serialised += JsonSerializer.Serialize(members);
        Saves++;
    }
}
