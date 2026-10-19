using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Security.Authentication;
using System.Threading.Channels;

namespace Promin.Protocol.Transport;

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
    /// Ceiling on a single inbound frame. A 1080p keyframe out of the encoder sits far
    /// below this; the bound exists so a peer cannot stream unbounded bytes into the receiver's
    /// memory on one stream. An offending stream is aborted and dropped, and the connection
    /// carries on — every other frame is on its own stream and is unaffected.
    /// </summary>
    public const int MaxVideoFrameBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Ceiling on one screen picture from an agent PC (the console's side). A 480×300 JPEG is tens of KB; anything
    /// past this is refused like an oversized frame.
    /// </summary>
    public const int MaxPictureBytes = 1024 * 1024;

    /// <summary>
    /// How many inbound frames wait to be picked up unless the creator says otherwise. The queue is
    /// deliberately small and DropOldest: if the consumer falls behind, the freshest frames are the ones
    /// worth keeping — a mirrored room screen wants to be current, not complete. A frame lost this
    /// way leaves a gap in the frame ids, which the receiving session treats as a lost frame.
    /// </summary>
    public const int DefaultInboundFrameQueueCapacity = 8;

    private readonly QuicConnection _connection;
    private readonly Channel<QuicStream> _inboundControlStreams = Channel.CreateUnbounded<QuicStream>();
    private readonly Channel<byte[]> _inboundVideoFrames;
    private readonly SemaphoreSlim _drainSlots = new(
        QuicTransportSettings.MaxConcurrentInboundVideoStreams,
        QuicTransportSettings.MaxConcurrentInboundVideoStreams);
    private readonly int _maxInboundFrameBytes;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly Task _acceptLoop;
    private int _disposed;
    private int _activeVideoDrains;

    /// <summary>How many inbound video streams are being read into memory right now.</summary>
    internal int ActiveVideoDrains => Volatile.Read(ref _activeVideoDrains);

    /// <summary>Complete inbound frames waiting for <see cref="ReceiveVideoFrameAsync"/>.</summary>
    internal int QueuedVideoFrames => _inboundVideoFrames.Reader.Count;

    /// <summary>
    /// Channels are made by <see cref="QuicMasterConnector"/> and <see cref="QuicAgentListener"/>
    /// only. <paramref name="pinnedDeviceId"/> is what they found by looking the presented
    /// certificate up in their pins; it is not something a caller gets to assert.
    /// </summary>
    /// <param name="acceptsStreams">
    /// False for a pairing connection the master dials, which grants its peer no streams of
    /// either kind: there is nothing to accept, and .NET refuses to wait for a stream on
    /// such a connection. No stream the peer opens then arrives on this channel. What the peer
    /// writes on a stream this side opened still does: the replies on the control stream.
    /// </param>
    /// <param name="maxInboundFrameBytes">
    /// How large one inbound stream may be: <see cref="MaxVideoFrameBytes"/> for broadcast video,
    /// <see cref="MaxPictureBytes"/> for screen pictures.
    /// </param>
    internal QuicChannel(
        QuicConnection connection,
        string? pinnedDeviceId,
        int inboundFrameQueueCapacity = DefaultInboundFrameQueueCapacity,
        bool acceptsStreams = true,
        int maxInboundFrameBytes = MaxVideoFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(inboundFrameQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInboundFrameBytes, 1);
        _connection = connection;
        _maxInboundFrameBytes = maxInboundFrameBytes;
        _inboundVideoFrames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(inboundFrameQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
        PinnedDeviceId = pinnedDeviceId;
        if (connection.RemoteCertificate is { } certificate)
        {
            RemoteCertificate = certificate;
            RemoteCertificateThumbprint = PeerCertificateStore.ComputeThumbprint(certificate);
            RemoteCertificateSubjectName = CertificateIdentity.ReadDeviceId(certificate);
        }

        _acceptLoop = acceptsStreams ? RunAcceptLoopAsync(_acceptCts.Token) : Task.CompletedTask;
    }

    /// <summary>
    /// The device id whose pin the certificate the peer presented matches, as judged by this
    /// side's own pins. It means the same thing on both sides: <b>this side has authenticated
    /// the peer as that device.</b>
    /// </summary>
    /// <remarks>
    /// What it does <b>not</b> mean is that the peer accepted this side. TLS 1.3 validates the
    /// client certificate after the client's handshake has already completed, so a master the
    /// agent refuses still gets a connected channel, with this property set, and is only told
    /// when it uses the connection. A completed connection is therefore not a completed pairing
    /// and not proof of acceptance: pairing is reported by <see cref="PairingOutcome"/>, and
    /// acceptance by the first control message that comes back.
    /// </remarks>
    public string? PinnedDeviceId { get; }

    /// <summary>SHA-256 thumbprint of the certificate the peer actually presented.</summary>
    public string? RemoteCertificateThumbprint { get; }

    /// <summary>
    /// The certificate the peer actually presented on this TLS connection — the one a pairing
    /// handshake over this channel has to be bound to.
    /// </summary>
    internal System.Security.Cryptography.X509Certificates.X509Certificate? RemoteCertificate { get; }

    /// <summary>
    /// The subject name on the certificate the peer presented. Supplied by the peer and proof
    /// of nothing: never use it to decide who the peer is — that is <see cref="PinnedDeviceId"/>.
    /// It exists so a pairing operation knows what name to pin a newly proven master under.
    /// </summary>
    internal string? RemoteCertificateSubjectName { get; }

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public async Task<QuicControlChannel> OpenControlChannelAsync(CancellationToken ct)
    {
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct)
            .ConfigureAwait(false);
        return new QuicControlChannel(stream);
    }

    /// <summary>
    /// The next bidirectional stream the peer opened. On an agent's connection from a console
    /// PC that is the control stream or a file send; only the stream's first message tells which.
    /// </summary>
    /// <remarks>
    /// An outbound QUIC stream is invisible to the peer until its opener writes to it, so the
    /// side that accepts here must be the side that waits to receive first in the exchange that
    /// follows. The master opens the control stream and each send and speaks first on them; the
    /// agent accepts.
    /// </remarks>
    public async Task<QuicControlChannel> AcceptControlChannelAsync(CancellationToken ct) =>
        new(await _inboundControlStreams.Reader.ReadAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Sends one whole encoded frame (envelope followed by the encoded bytes)
    /// as its own unidirectional stream. Cancelling <paramref name="ct"/>
    /// aborts that stream's write side — this is the per-client backpressure
    /// mechanism the broadcast session uses when a client falls behind.
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
        catch (AuthenticationException)
        {
            // The peer refused this side's certificate. TLS 1.3 validates the client certificate
            // after the client's handshake has completed, so a master the agent rejects gets a
            // channel first and this afterwards. The connection is gone; the owner finds out
            // when it uses the channel. Letting this escape would make DisposeAsync throw it.
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
                // Wait for a slot before reading a single byte. A stream that is waiting holds
                // no frame buffer, and QUIC flow control holds its sender, so the memory a peer
                // can make this side allocate is bounded by the slot count rather than by how
                // many streams it has credit to open. The wait is here and not in the accept
                // pump so that queued video can never delay a control stream being accepted.
                await _drainSlots.WaitAsync(_acceptCts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _activeVideoDrains);
                try
                {
                    var frame = await ReadWholeFrameAsync(stream, _maxInboundFrameBytes, _acceptCts.Token).ConfigureAwait(false);
                    if (frame is not null)
                    {
                        _inboundVideoFrames.Writer.TryWrite(frame);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeVideoDrains);
                    _drainSlots.Release();
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
    /// <paramref name="maxBytes"/>.
    /// </summary>
    private static async Task<byte[]?> ReadWholeFrameAsync(QuicStream stream, int maxBytes, CancellationToken ct)
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

            if (buffer.WrittenCount > maxBytes)
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

        // Every drain has finished by now: the accept loop awaits them all before it completes.
        _acceptCts.Dispose();
        _drainSlots.Dispose();

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // already torn down by the peer
        }
        catch (AuthenticationException)
        {
            // torn down by the peer refusing this side's certificate
        }
    }
}
