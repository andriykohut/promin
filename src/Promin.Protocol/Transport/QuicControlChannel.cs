using System.IO.Pipelines;
using System.Net.Quic;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Protocol.Transport;

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
///
/// A file send reuses a stream as one long exchange: messages interleaved with raw file bytes
/// that the receiver reads with <see cref="ReceiveBytesAsync"/>. The raw bytes are unframed, so
/// the messages around them say how many to expect.
/// </remarks>
public sealed class QuicControlChannel : IControlChannel, IAsyncDisposable
{
    /// <summary>Application error code used when this stream is abandoned as corrupt.</summary>
    public const long CorruptFrameErrorCode = 0x2;

    /// <summary>Application error code used when this stream is abandoned after a failed pairing.</summary>
    public const long PairingFailedErrorCode = 0x3;

    /// <summary>Application error code used when a receiver refuses the bytes a sender is still writing.</summary>
    public const long RefusedErrorCode = 0x4;

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
        await SendBytesAsync(ControlMessageCodec.Encode(message), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes raw bytes onto the stream, between whole messages. Serialised with
    /// <see cref="SendAsync"/>, so the bytes never land inside a frame.
    /// </summary>
    public async Task SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
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
    /// <exception cref="ControlChannelClosedException">
    /// The peer closed the stream cleanly before a whole message arrived.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This channel was already disposed — a lifecycle bug in the caller, deliberately not
    /// reported as the peer having gone away.
    /// </exception>
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
                    throw new ControlChannelClosedException(
                        "QUIC control stream closed before a full message arrived.");
                }
            }
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="count"/> raw bytes into <paramref name="destination"/>,
    /// starting with any already buffered behind the last message. Whatever follows them stays
    /// buffered for the next <see cref="ReceiveAsync"/>.
    /// </summary>
    /// <exception cref="ControlChannelClosedException">
    /// The peer closed the stream cleanly before <paramref name="count"/> bytes arrived.
    /// </exception>
    public async Task ReceiveBytesAsync(Stream destination, long count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        await _receiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (count > 0)
            {
                var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.Start);
                    throw new ControlChannelClosedException(
                        $"QUIC control stream closed with {count} bytes still to come.");
                }

                var taken = buffer.Slice(0, Math.Min(buffer.Length, count));
                try
                {
                    foreach (var segment in taken)
                    {
                        await destination.WriteAsync(segment, ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    _reader.AdvanceTo(buffer.Start);
                    throw;
                }

                count -= taken.Length;
                _reader.AdvanceTo(taken.End);
            }
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    /// <summary>
    /// Tells the peer to stop sending on this stream: its pending and later writes fail with
    /// <see cref="RefusedErrorCode"/>. This side can still send, so a refusal can be explained.
    /// Safe to call on a stream that is already gone.
    /// </summary>
    public void StopReceiving()
    {
        try
        {
            _stream.Abort(QuicAbortDirection.Read, RefusedErrorCode);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (QuicException)
        {
        }
    }

    private void AbortAsCorrupt() => Abort(CorruptFrameErrorCode);

    /// <summary>
    /// Abandons the stream in both directions, so the peer's pending read or write on it fails
    /// at once with <paramref name="errorCode"/> instead of waiting on a stream nobody will use
    /// again. The channel still has to be disposed afterwards. Safe to call on a stream that is
    /// already gone.
    /// </summary>
    public void Abort(long errorCode)
    {
        try
        {
            _stream.Abort(QuicAbortDirection.Both, errorCode);
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
        catch (System.Security.Authentication.AuthenticationException)
        {
            // The peer refused this side's certificate, which TLS 1.3 reports after the fact and
            // on whatever touches the connection next. There is nothing to shut down.
        }

        _sendGate.Dispose();
        _receiveGate.Dispose();
    }
}
