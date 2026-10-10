namespace Promin.Protocol.Broadcast;

/// <summary>
/// One connected agent PC, as far as <see cref="BroadcastSession"/> is concerned.
/// </summary>
public interface IVideoSink
{
    /// <summary>
    /// Sends one whole frame (envelope followed by the encoded bytes) as its
    /// own unidirectional QUIC stream, and completes when the peer has
    /// acknowledged it. That wait is the session's backpressure signal.
    /// Cancelling <paramref name="ct"/> must abort that stream's write side and
    /// throw <see cref="OperationCanceledException"/>, not silently succeed: it
    /// is how a client that has fallen behind sheds a frame.
    /// </summary>
    /// <remarks>
    /// Any other exception means the client is gone; the session reports it
    /// through <see cref="BroadcastSession.ClientDisconnected"/>.
    /// </remarks>
    ValueTask SendFrameAsync(byte[] envelopeAndFrame, CancellationToken ct);
}
