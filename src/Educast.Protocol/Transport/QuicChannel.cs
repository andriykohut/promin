using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Educast.Protocol.Transport;

/// <summary>
/// One QUIC connection to one peer. Control messages ride a single reliable
/// bidirectional stream; every encoded video frame rides its own unidirectional
/// stream. .NET exposes no QUIC datagram API, and per-frame streams give the
/// property datagrams were wanted for anyway: streams are mutually independent,
/// so one slow or aborted frame never blocks the frames behind it.
///
/// Both kinds of stream arrive through the same <c>AcceptInboundStreamAsync</c>
/// queue, so this class runs a single accept pump that dispatches by stream
/// type — bidirectional to the control channel, unidirectional to the frame
/// queue. Never call <c>AcceptInboundStreamAsync</c> on the wrapped connection
/// from anywhere else.
/// </summary>
public sealed class QuicChannel : IAsyncDisposable
{
    /// <summary>Application error code used when abandoning a frame that fell too far behind.</summary>
    public const long VideoStreamAbortErrorCode = 0x1;

    /// <summary>
    /// Ceiling on a single inbound frame. A 1080p keyframe out of the Task 6 encoder sits far
    /// below this; the bound exists so a peer cannot stream unbounded bytes into the receiver's
    /// memory on one stream. An offending stream is aborted and dropped, and the connection
    /// carries on — every other frame is on its own stream and is unaffected.
    /// </summary>
    public const int MaxVideoFrameBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Inbound frames waiting to be picked up. Deliberately small and DropOldest: if the
    /// consumer falls behind, the freshest frames are the ones worth keeping — a mirrored
    /// classroom screen wants to be current, not complete.
    /// </summary>
    private const int InboundFrameQueueCapacity = 8;

    private readonly QuicConnection _connection;
    private readonly Channel<QuicStream> _inboundControlStreams = Channel.CreateUnbounded<QuicStream>();
    private readonly Channel<byte[]> _inboundVideoFrames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(InboundFrameQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    public QuicChannel(QuicConnection connection, string? remoteDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        RemoteDeviceId = remoteDeviceId;
        RemoteCertificateThumbprint = connection.RemoteCertificate is { } certificate
            ? certificate.GetCertHashString(HashAlgorithmName.SHA256)
            : null;
        _acceptLoop = RunAcceptLoopAsync(_acceptCts.Token);
    }

    /// <summary>
    /// The device id this peer was pinned under, when the connection was established against an
    /// existing pin. Null during trust-on-first-use pairing, where the peer identity is only
    /// established by the pre-shared-key exchange that follows.
    /// </summary>
    public string? RemoteDeviceId { get; }

    /// <summary>
    /// SHA-256 thumbprint of the certificate the peer actually presented — what the caller pins
    /// via <see cref="PeerCertificateStore.Pin"/> once pairing has proved who the peer is.
    /// </summary>
    public string? RemoteCertificateThumbprint { get; }

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public async Task<QuicControlChannel> OpenControlChannelAsync(CancellationToken ct)
    {
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct)
            .ConfigureAwait(false);
        return new QuicControlChannel(stream);
    }

    /// <remarks>
    /// An outbound QUIC stream is invisible to the peer until its opener writes to it, so the
    /// side that accepts here must be the side that waits to receive first in the exchange that
    /// follows. The master opens the control stream and speaks first; the agent accepts.
    /// </remarks>
    public async Task<QuicControlChannel> AcceptControlChannelAsync(CancellationToken ct) =>
        new(await _inboundControlStreams.Reader.ReadAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Sends one whole encoded frame (envelope followed by the encoded bytes)
    /// as its own unidirectional stream. Cancelling <paramref name="ct"/>
    /// aborts that stream's write side — this is the per-client backpressure
    /// mechanism Task 9 drives when a client falls behind.
    /// </summary>
    public async ValueTask SendVideoFrameAsync(ReadOnlyMemory<byte> envelopeAndFrame, CancellationToken ct)
    {
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct)
            .ConfigureAwait(false);

        // The abort has to cover DisposeAsync below as well: it waits for the peer to
        // acknowledge the stream shutdown and takes no cancellation token of its own, and that
        // wait is exactly where a stalled client would otherwise hang the sender.
        var abortOnCancel = ct.Register(static state => AbortWrite((QuicStream)state!), stream);
        try
        {
            await stream.WriteAsync(envelopeAndFrame, completeWrites: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbortWrite(stream);
            throw;
        }
        finally
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (QuicException)
            {
                // The stream or the connection was aborted; this frame is gone, which is the
                // intended outcome of cancelling a send.
            }

            await abortOnCancel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the next complete inbound frame. One accepted unidirectional
    /// stream, read to end, is exactly one frame — QUIC already handled
    /// fragmentation, ordering, and retransmission inside it.
    /// </summary>
    public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) =>
        _inboundVideoFrames.Reader.ReadAsync(ct);

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        var drains = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);

                if (stream.Type == QuicStreamType.Bidirectional)
                {
                    await _inboundControlStreams.Writer.WriteAsync(stream, ct).ConfigureAwait(false);
                }
                else
                {
                    drains.RemoveAll(static drain => drain.IsCompleted);
                    drains.Add(DrainVideoStreamAsync(stream));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on DisposeAsync
        }
        catch (QuicException)
        {
            // the peer closed the connection
        }
        catch (ObjectDisposedException)
        {
            // the connection was disposed underneath the pump
        }
        finally
        {
            _inboundControlStreams.Writer.TryComplete();
            try
            {
                await Task.WhenAll(drains).ConfigureAwait(false);
            }
            catch
            {
                // DrainVideoStreamAsync swallows its own faults; this is belt and braces.
            }

            _inboundVideoFrames.Writer.TryComplete();
        }
    }

    private async Task DrainVideoStreamAsync(QuicStream stream)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var frame = await ReadWholeFrameAsync(stream, _acceptCts.Token).ConfigureAwait(false);
                if (frame is not null)
                {
                    _inboundVideoFrames.Writer.TryWrite(frame);
                }
            }
            catch (QuicException)
            {
                // The sender aborted this frame's stream because this client fell behind.
                // Discard the partial frame: every other frame is on its own stream and is
                // completely unaffected.
            }
            catch (OperationCanceledException)
            {
                // channel disposed
            }
            catch (ObjectDisposedException)
            {
                // channel disposed
            }
        }
    }

    /// <summary>
    /// Reads one stream to its end, or aborts it and returns null if the peer exceeds
    /// <see cref="MaxVideoFrameBytes"/>.
    /// </summary>
    private static async Task<byte[]?> ReadWholeFrameAsync(QuicStream stream, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);

        while (true)
        {
            var destination = buffer.GetMemory(16 * 1024);
            var read = await stream.ReadAsync(destination, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenSpan.ToArray();
            }

            buffer.Advance(read);

            if (buffer.WrittenCount > MaxVideoFrameBytes)
            {
                stream.Abort(QuicAbortDirection.Read, VideoStreamAbortErrorCode);
                return null;
            }
        }
    }

    private static void AbortWrite(QuicStream stream)
    {
        try
        {
            stream.Abort(QuicAbortDirection.Write, VideoStreamAbortErrorCode);
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

        await _acceptCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        while (_inboundControlStreams.Reader.TryRead(out var orphan))
        {
            await orphan.DisposeAsync().ConfigureAwait(false);
        }

        _acceptCts.Dispose();

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // already torn down by the peer
        }
    }
}
