using Educast.Master.Core.Rooms;
using Educast.Protocol.Video;

namespace Educast.Master.Core.Wall;

public readonly record struct BroadcastStats(int FramesDropped, int FramesOffered);

/// <summary>The broadcast, as the wall drives it: the pipeline and one connection per student PC.</summary>
public interface IBroadcastController
{
    /// <summary>From the probe until an encoder has been built; from then on what the encoder reports.</summary>
    bool HardwareEncoderAvailable { get; }

    /// <summary>The primary display's pixel size, from which the rate the picker shows is computed.</summary>
    PixelSize DisplaySize { get; }

    /// <summary>Raised on any thread when a PC that was receiving stops.</summary>
    event Action<string>? ClientLost;

    /// <summary>
    /// Raised on any thread when the picture source failed on air. The broadcast has been stopped
    /// (as by <see cref="Stop"/>) when this is raised.
    /// </summary>
    event Action? BroadcastFailed;

    /// <summary>
    /// Connects to each PC and starts sending. Returns the ids that could not be reached. Throws
    /// when the screen cannot be captured or the encoder cannot be built.
    /// </summary>
    Task<IReadOnlyCollection<string>> StartAsync(
        IReadOnlyCollection<RoomMember> pcs, QualityPreset preset, Sharpness sharpness, CancellationToken ct);

    /// <summary>Adds one PC to the running broadcast. False when it could not be reached.</summary>
    Task<bool> AddAsync(RoomMember pc, CancellationToken ct);

    void StopSendingTo(string deviceId);

    void Stop();

    BroadcastStats StatsFor(string deviceId);
}
