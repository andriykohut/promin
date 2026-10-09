using Educast.Master.Core.Rooms;
using Educast.Master.Core.Wall;
using Educast.Protocol.Video;

namespace Educast.Master.Core.Tests.TestDoubles;

/// <summary>
/// A broadcast controller that records every call, answers from a table, and can fail the way
/// the real one can: name PCs it could not reach, throw at start, lose a client, or lose the picture.
/// </summary>
public sealed class FakeBroadcastController : IBroadcastController
{
    public bool HardwareEncoderAvailable { get; set; } = true;

    public PixelSize DisplaySize { get; set; } = new(1920, 1080);

    public event Action<string>? ClientLost;

    public event Action? BroadcastFailed;

    public List<(IReadOnlyCollection<RoomMember> Pcs, QualityPreset Preset, Sharpness Sharpness)> Starts { get; } = [];

    public List<RoomMember> Adds { get; } = [];

    public List<string> StoppedSendingTo { get; } = [];

    public int Stops { get; private set; }

    /// <summary>Device ids that <see cref="StartAsync"/> reports unreachable and <see cref="AddAsync"/> refuses.</summary>
    public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, BroadcastStats> Stats { get; } = new(StringComparer.Ordinal);

    /// <summary>Thrown by <see cref="StartAsync"/> when set.</summary>
    public Exception? StartThrows { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> returns its task instead of answering at once: the connect is still running.</summary>
    public TaskCompletionSource<IReadOnlyCollection<string>>? PendingStart { get; set; }

    public Task<IReadOnlyCollection<string>> StartAsync(
        IReadOnlyCollection<RoomMember> pcs, QualityPreset preset, Sharpness sharpness, CancellationToken ct)
    {
        Starts.Add((pcs, preset, sharpness));
        if (StartThrows is not null)
        {
            throw StartThrows;
        }

        return PendingStart?.Task ?? Task.FromResult<IReadOnlyCollection<string>>(
            pcs.Select(p => p.DeviceId).Where(Unreachable.Contains).ToList());
    }

    public Task<bool> AddAsync(RoomMember pc, CancellationToken ct)
    {
        Adds.Add(pc);
        return Task.FromResult(!Unreachable.Contains(pc.DeviceId));
    }

    public void StopSendingTo(string deviceId) => StoppedSendingTo.Add(deviceId);

    public void Stop() => Stops++;

    public BroadcastStats StatsFor(string deviceId) => Stats.GetValueOrDefault(deviceId);

    public void RaiseLost(string deviceId) => ClientLost?.Invoke(deviceId);

    public void RaiseFailed() => BroadcastFailed?.Invoke();
}
