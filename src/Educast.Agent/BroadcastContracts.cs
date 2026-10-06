using Educast.Protocol.Messages;

namespace Educast.Agent;

/// <summary>
/// One teacher PC's connection as the agent handles it: its control messages and its video frames. In the agent
/// this is a <c>QuicChannel</c>; the seam keeps the handling testable with no network.
/// </summary>
internal interface IAgentConnection
{
    /// <summary>
    /// The next control message. The first call waits for the teacher PC to open its control stream, which it does
    /// by sending its first message. Throws when the connection is lost or its control stream is corrupt.
    /// </summary>
    Task<ControlMessage> ReceiveControlAsync(CancellationToken ct);

    /// <summary>The next whole video frame (envelope and encoded bytes). Throws when the connection has ended.</summary>
    ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct);
}

/// <summary>What shows one broadcast on the student's screen: a window, a decoder, and the session that decides what reaches them.</summary>
internal interface IBroadcastDisplay : IDisposable
{
    /// <summary>False once the display has ended by itself (the student closed the window, the GPU failed) or been disposed.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Hands the display one video stream's payload, to be shown in its turn, in the order given. Waits while the
    /// display is behind. Completes without effect once the display has ended.
    /// </summary>
    ValueTask SubmitFrameAsync(byte[] streamPayload, CancellationToken ct);

    /// <summary>A new stream begins on the same display (the teacher PC restarted its broadcast): frames submitted before this are shown first.</summary>
    ValueTask RestartAsync(CancellationToken ct);
}

internal interface IBroadcastDisplayFactory
{
    /// <summary>
    /// Makes the display for a broadcast and returns it once its window exists, or null when it cannot be made
    /// (the reason is logged). Never leaves a window behind on failure.
    /// </summary>
    Task<IBroadcastDisplay?> StartAsync(StartBroadcast message);
}
