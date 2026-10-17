using Promin.Master.Core.Rooms;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class FakeRoomConnections : IRoomConnections
{
    public event Action<string, byte[]>? PictureArrived;

    public event Action<string>? Ended;

    public List<RoomMember> Wanted { get; } = [];

    public List<string> Dropped { get; } = [];

    public void Want(RoomMember pc) => Wanted.Add(pc);

    public void Drop(string deviceId) => Dropped.Add(deviceId);

    public void RaisePicture(string deviceId, byte[] picture) => PictureArrived?.Invoke(deviceId, picture);

    public void RaiseEnded(string deviceId) => Ended?.Invoke(deviceId);
}
