using Promin.Protocol.Messages;

namespace Promin.Master.Core.Control;

/// <summary>The agent PC refused to be controlled.</summary>
public sealed class ControlRefusedException() : Exception("The PC refused to be controlled.");

/// <summary>
/// The console PC's control of one agent PC: its screen as video, and the messages both ways on the remote stream.
/// Disposing closes the remote stream, which ends control.
/// </summary>
public interface IControlSession : IAsyncDisposable
{
    /// <summary>The next video frame: <c>VideoFrameEnvelope</c> and H.264, without the kind byte. Throws once the session or the connection has ended.</summary>
    ValueTask<byte[]> ReceiveFrameAsync(CancellationToken ct);

    /// <summary>The next <see cref="ControlState"/> or <see cref="ClipboardText"/> from the agent PC. Throws once the stream or the connection has ended.</summary>
    Task<ControlMessage> ReceiveAsync(CancellationToken ct);

    /// <summary>Sends a message to the agent PC. Safe for concurrent callers.</summary>
    Task SendAsync(ControlMessage message, CancellationToken ct);
}
