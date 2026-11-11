using Promin.Master.Core.Control;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class FakeRoomConnections : IRoomConnections
{
    public event Action<string, byte[]>? PictureArrived;

    public event Action<string>? Ended;

    public event Action<string, bool>? LockReported;

    public List<RoomMember> Wanted { get; } = [];

    public List<string> Dropped { get; } = [];

    public List<(string Id, string Message)> LocksSent { get; } = [];

    public List<string> UnlocksSent { get; } = [];

    public List<string> ControlStarts { get; } = [];

    /// <summary>What <see cref="StartControlAsync"/> hands out; null makes the start fail.</summary>
    public IControlSession? NextSession { get; set; }

    private readonly Dictionary<string, TaskCompletionSource> _drops = new(StringComparer.Ordinal);

    public void Want(RoomMember pc) => Wanted.Add(pc);

    public void Drop(string deviceId)
    {
        Dropped.Add(deviceId);
        DropSource(deviceId).TrySetResult();
    }

    /// <summary>Completes once <see cref="Drop"/> was called for the id.</summary>
    public Task Dropping(string deviceId) => DropSource(deviceId).Task;

    public void Lock(string deviceId, string message) => LocksSent.Add((deviceId, message));

    public Task UnlockAsync(string deviceId)
    {
        UnlocksSent.Add(deviceId);
        return Task.CompletedTask;
    }

    public Task<IControlSession?> StartControlAsync(string deviceId)
    {
        ControlStarts.Add(deviceId);
        return Task.FromResult(NextSession);
    }

    public void RaiseLock(string deviceId, bool locked) => LockReported?.Invoke(deviceId, locked);

    private TaskCompletionSource DropSource(string deviceId)
    {
        lock (_drops)
        {
            if (!_drops.TryGetValue(deviceId, out var source))
            {
                _drops[deviceId] = source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return source;
        }
    }

    public void RaisePicture(string deviceId, byte[] picture) => PictureArrived?.Invoke(deviceId, picture);

    public void RaiseEnded(string deviceId) => Ended?.Invoke(deviceId);
}
