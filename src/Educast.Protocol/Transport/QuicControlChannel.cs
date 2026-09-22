using System.IO.Pipelines;
using System.Net.Quic;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;

namespace Educast.Protocol.Transport;

/// <summary>
/// The reliable control stream: one QUIC bidirectional stream carrying length-prefixed
/// control messages in both directions. Video never touches this stream.
/// </summary>
/// <remarks>
/// Reads honour the three-way contract of <see cref="ControlMessageCodec.TryDecode"/>:
/// <c>false</c> means buffer more bytes and retry, <c>true</c> means a message was decoded,
/// and a <see cref="ControlMessageFormatException"/> means the peer's framing is definitively
/// corrupt. Corruption aborts this stream and is rethrown so the owner can tear down this one
/// connection — it must never be allowed to escape as far as the listener or another client's
/// session. Buffering is bounded by <see cref="ControlMessageCodec.MaxFrameSize"/>, so a peer
/// cannot make the receiver accumulate memory by promising a frame it never sends.
///
/// Sends are serialised; concurrent senders would otherwise interleave two frames' bytes on
/// the wire. Receives are single-consumer and also serialised.
/// </remarks>
public sealed class QuicControlChannel : IControlChannel, IAsyncDisposable
{
    /// <summary>Application error code used when this stream is abandoned as corrupt.</summary>
    public const long CorruptFrameErrorCode = 0x2;

    private readonly QuicStream _stream;
    private readonly PipeReader _reader;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private int _disposed;

    public QuicControlChannel(QuicStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    }

    public async Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var encoded = ControlMessageCodec.Encode(message);

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(encoded, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <exception cref="ControlMessageFormatException">
    /// The peer's framing is corrupt. This stream has been aborted; the caller must tear down
    /// this connection only.
    /// </exception>
    /// <exception cref="InvalidOperationException">The peer closed the stream mid-message.</exception>
    public async Task<ControlMessage> ReceiveAsync(CancellationToken ct)
    {
        await _receiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                try
                {
                    if (ControlMessageCodec.TryDecode(buffer, out var message, out var consumed))
                    {
                        _reader.AdvanceTo(buffer.GetPosition(consumed));
                        return message!;
                    }
                }
                catch (ControlMessageFormatException)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    AbortAsCorrupt();
                    throw;
                }

                // TryDecode returned false: incomplete. A frame that is still incomplete once
                // more than a whole maximum frame has arrived cannot ever complete.
                if (buffer.Length > ControlMessageCodec.MaxFrameSize + 4L)
                {
                    _reader.AdvanceTo(buffer.Start, buffer.End);
                    AbortAsCorrupt();
                    throw new ControlMessageFormatException(
                        $"Buffered {buffer.Length} bytes without completing a control frame; " +
                        $"the maximum frame is {ControlMessageCodec.MaxFrameSize} bytes.");
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "QUIC control stream closed before a full message arrived.");
                }
            }
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    private void AbortAsCorrupt()
    {
        try
        {
            _stream.Abort(QuicAbortDirection.Both, CorruptFrameErrorCode);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (QuicException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _reader.CompleteAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // The connection is already gone; nothing left to shut down gracefully.
        }

        _sendGate.Dispose();
        _receiveGate.Dispose();
    }
}
