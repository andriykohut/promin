namespace Promin.Master.Core.Rooms;

/// <summary>
/// Where the room lives on the console PC: the paired agent PCs and what the presenter calls
/// them. Never the room code.
/// </summary>
public interface IRoomStore
{
    IReadOnlyList<RoomMember> Load();

    void Save(IReadOnlyList<RoomMember> members);
}
