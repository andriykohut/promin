using Promin.Master.Core.Control;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Updates;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class FakeRoomConnections : IRoomConnections
{
    public event Action<string, byte[]>? PictureArrived;

    public event Action<string>? Ended;

    public event Action<string, bool>? LockReported;

    public event Action<string, bool>? PowerAnswered;

    public event Action<string, ReleaseVersion>? VersionReported;

    public List<(string Id, UpdateFile File)> UpdatesSent { get; } = [];

    /// <summary>What <see cref="UpdateAsync"/> answers for a PC; Accepted unless set.</summary>
    public Func<string, UpdateOutcome?> UpdateAnswer { get; set; } = _ => UpdateOutcome.Accepted;

    public List<RoomMember> Wanted { get; } = [];

    public List<string> Dropped { get; } = [];

    public List<(string Id, string Message)> LocksSent { get; } = [];

    public List<string> UnlocksSent { get; } = [];

    public List<(string Id, PowerAction Action)> PowersSent { get; } = [];

    public List<string> SessionStarts { get; } = [];

    /// <summary>What <see cref="StartViewingAsync"/> hands out; null makes the start fail.</summary>
    public IControlSession? NextSession { get; set; }

    /// <summary>When set, what <see cref="StartViewingAsync"/> returns in place of <see cref="NextSession"/>, for a start still under way.</summary>
    public Task<IControlSession?>? NextStart { get; set; }

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

    public void Power(string deviceId, PowerAction action) => PowersSent.Add((deviceId, action));

    public Task<IControlSession?> StartViewingAsync(string deviceId)
    {
        SessionStarts.Add(deviceId);
        return NextStart ?? Task.FromResult(NextSession);
    }

    public Task<UpdateOutcome?> UpdateAsync(string deviceId, UpdateFile file)
    {
        UpdatesSent.Add((deviceId, file));
        return Task.FromResult(UpdateAnswer(deviceId));
    }

    public void RaiseVersion(string deviceId, string version) =>
        VersionReported?.Invoke(deviceId, ReleaseVersion.TryParse(version, out var parsed) ? parsed : throw new FormatException(version));

    public void RaiseLock(string deviceId, bool locked) => LockReported?.Invoke(deviceId, locked);

    public void RaisePower(string deviceId, bool accepted) => PowerAnswered?.Invoke(deviceId, accepted);

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
