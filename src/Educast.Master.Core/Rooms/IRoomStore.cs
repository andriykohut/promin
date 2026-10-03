namespace Educast.Master.Core.Rooms;

/// <summary>
/// Where the room lives on the teacher PC: the paired student PCs and what the teacher calls
/// them. Never the room code.
/// </summary>
public interface IRoomStore
{
    IReadOnlyList<RoomMember> Load();

    void Save(IReadOnlyList<RoomMember> members);
}
