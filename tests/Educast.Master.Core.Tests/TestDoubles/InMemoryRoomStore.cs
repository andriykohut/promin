using System.Text.Json;
using Educast.Master.Core.Rooms;

namespace Educast.Master.Core.Tests.TestDoubles;

/// <summary>
/// A room store that keeps the members in memory and, like the real one, serialises every save
/// — so a test can look at the bytes that would have reached the disk.
/// </summary>
public sealed class InMemoryRoomStore : IRoomStore
{
    public List<RoomMember> Members { get; set; } = [];

    /// <summary>Everything every <see cref="Save"/> would have written, concatenated.</summary>
    public string Serialised { get; private set; } = string.Empty;

    public int Saves { get; private set; }

    public IReadOnlyList<RoomMember> Load() => Members.ToList();

    public void Save(IReadOnlyList<RoomMember> members)
    {
        Members = members.ToList();
        Serialised += JsonSerializer.Serialize(members);
        Saves++;
    }
}
