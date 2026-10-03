using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Educast.Protocol.Messages;

namespace Educast.Protocol.Transport;

/// <summary>
/// The agent (client machine) side of the transport: listens for the master to dial in and
/// hands back one <see cref="QuicChannel"/> per accepted connection, or pairs with a master.
/// </summary>
/// <remarks>
/// Two operations, and they do not overlap:
///
/// <see cref="AcceptAsync"/> is every ordinary connection. Masters are pinned by their real
/// device id, like every other peer. The dialling master is unknown until TLS presents its
/// certificate, so the thumbprint is resolved back to a device id through
/// <see cref="PeerCertificateStore.TryGetDeviceIdForThumbprint"/>; a certificate that resolves
/// to no pin is refused during the handshake and never reaches the protocol. Nothing about this
/// path ever trusts a certificate on first use.
///
/// <see cref="AcceptPairingAsync"/> is the only way a master comes to be pinned. It opens a
/// pairing window that admits one unpinned certificate, runs the mutual pre-shared-key handshake
/// with whoever presented it, and pins that certificate if and only if the handshake succeeded.
/// The connection a window admits goes to the pairing operation and nowhere else:
/// <see cref="AcceptAsync"/> never returns a connection whose certificate is not pinned.
///
/// Connections are taken off the QUIC listener by a single background
/// <see cref="QuicAcceptPump"/>, started by <see cref="StartAsync"/>; see that type for how
/// refused peers, runs of failures and local faults are told apart.
/// </remarks>
public sealed class QuicAgentListener : IAsyncDisposable
{
    /// <summary>
    /// Established connections waiting for <see cref="AcceptAsync"/>. Bounded: if the
    /// application stops accepting, the pump waits rather than queueing without limit.
    /// </summary>
    private const int AcceptQueueCapacity = 16;

    private readonly X509Certificate2 _localCertificate;
    private readonly PeerCertificateStore _trustedMasters;
    private readonly int _port;
    private readonly IPAddress _bindAddress;
    private readonly Action<Exception>? _connectionRejected;
    private readonly Channel<QuicChannel> _established = Channel.CreateBounded<QuicChannel>(
        new BoundedChannelOptions(AcceptQueueCapacity) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly CancellationToken _shutdown;
    private QuicListener? _listener;
    private QuicAcceptPump? _pump;
    private Task? _pumpTask;
    private PairingTrustWindow? _pairingWindow;
    private int _disposed;

    public QuicAgentListener(
        X509Certificate2 localCertificate,
        PeerCertificateStore trustedMasters,
        int port,
        IPAddress? bindAddress = null,
        Action<Exception>? connectionRejected = null)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(trustedMasters);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _localCertificate = localCertificate;
        _trustedMasters = trustedMasters;
        _port = port;
        _bindAddress = bindAddress ?? IPAddress.Any;
        _connectionRejected = connectionRejected;
        _shutdown = _pumpCts.Token;
    }

    /// <summary>The endpoint actually bound, which resolves port 0 to the port the OS chose.</summary>
    public IPEndPoint LocalEndPoint =>
        _listener?.LocalEndPoint ?? throw new InvalidOperationException("The listener has not been started.");

    /// <summary>
    /// Handshakes refused since the last connection that was accepted. Zero on a healthy
    /// listener; a number that keeps climbing is a machine being dialled by something it does
    /// not trust, and is what a diagnostics view would show.
    /// </summary>
    public int ConsecutiveAcceptFailures => _pump?.ConsecutiveFailures ?? 0;

    public async Task StartAsync(CancellationToken ct)
    {
        QuicTransportSettings.EnsureSupported();

        if (_listener is not null)
        {
            throw new InvalidOperationException("The listener has already been started.");
        }

        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(_bindAddress, _port),
            ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(CreateConnectionOptions()),
        };

        var listener = await QuicListener.ListenAsync(options, ct).ConfigureAwait(false);
        _listener = listener;
        _pump = new QuicAcceptPump(listener.AcceptConnectionAsync, DeliverAsync, _connectionRejected);
        _pumpTask = RunPumpAsync(_pump, _shutdown);
    }

    /// <summary>
    /// Returns the next connection from a master that is already paired. The channel's
    /// <see cref="QuicChannel.PinnedDeviceId"/> is always set: it is the device id the master's
    /// certificate is pinned under.
    /// </summary>
    /// <remarks>
    /// A peer whose certificate is refused never appears here: the refusal is reported through
    /// the optional <c>connectionRejected</c> callback and the listener carries on. Neither does
    /// a master admitted by a pairing window — that connection belongs to
    /// <see cref="AcceptPairingAsync"/>. What does come out of this method, besides
    /// cancellation, is a fault of this machine's own — a credential that will not load, for
    /// instance. That is thrown to every caller from then on, because a listener in that state
    /// is broken, not busy.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The listener was disposed.</exception>
    public async Task<QuicChannel> AcceptAsync(CancellationToken ct)
    {
        EnsureStarted();

        try
        {
            return await _established.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException closed)
        {
            if (closed.InnerException is { } fault)
            {
                ExceptionDispatchInfo.Throw(fault);
            }

            throw new ObjectDisposedException(nameof(QuicAgentListener));
        }
    }

    /// <summary>
    /// Opens a pairing window and pairs with the one master that dials in during it: the mutual
    /// pre-shared-key handshake, and — on success, and only on success — a pin for the
    /// certificate that master presented, under the device id its certificate carries.
    /// </summary>
    /// <remarks>
    /// This is the only thing that lets an unpinned certificate through. The permission is
    /// <b>scoped to this one call</b>: it does not exist before it and is gone when it returns.
    /// It is <b>single-use</b>: the first unpinned certificate to arrive consumes it, whether
    /// that master then pairs or not, so a second stranger in the same window is refused. And it
    /// is <b>bounded</b> twice over, whatever token is passed: the window waits at most
    /// <paramref name="window"/> for a master to dial (default
    /// <see cref="QuicTransportSettings.DefaultPairingWindow"/>), and once one is admitted the
    /// handshake has <paramref name="handshakeTimeout"/> to finish (default five seconds).
    ///
    /// A master that is already pinned under the same name with a different certificate is not
    /// re-keyed; unpin it first. Masters that are already paired keep connecting normally
    /// through <see cref="AcceptAsync"/> while the window is open, and do not consume it.
    ///
    /// A failure the master or the network can cause comes back as an outcome with
    /// <see cref="PairingOutcome.Paired"/> false, not as an exception, and leaves the pins
    /// exactly as they were. The connection is closed either way; the master reconnects
    /// normally afterwards.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A pairing window is already open.</exception>
    public async Task<PairingOutcome> AcceptPairingAsync(
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? window = null,
        TimeSpan? handshakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(presharedKey);
        EnsureStarted();

        var lifetime = window ?? QuicTransportSettings.DefaultPairingWindow;
        var handshakeBudget = handshakeTimeout ?? QuicPairingHandshake.DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(handshakeBudget, TimeSpan.Zero);

        var trust = new PairingTrustWindow(_trustedMasters, expectedDeviceId: null, lifetime);
        if (Interlocked.CompareExchange(ref _pairingWindow, trust, null) is not null)
        {
            throw new InvalidOperationException("A pairing window is already open on this listener.");
        }

        QuicChannel? channel = null;
        try
        {
            QuicConnection connection;
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown))
            {
                waiting.CancelAfter(lifetime);
                try
                {
                    connection = await trust.Claimed.WaitAsync(waiting.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return PairingOutcome.Failed(_shutdown.IsCancellationRequested
                        ? "The listener was shut down while the pairing window was open."
                        : "No master dialled in before the pairing window closed.");
                }
            }

            channel = new QuicChannel(connection, pinnedDeviceId: null);
            if (channel.RemoteCertificate is not { } masterCertificate
                || channel.RemoteCertificateSubjectName is not { } deviceId
                || channel.RemoteCertificateThumbprint is not { } thumbprint)
            {
                return PairingOutcome.Failed("The master presented a certificate that names no device.");
            }

            // The certificate that actually arrived on this connection, and this side's own.
            var binding = CertificateBinding.Compute(masterCertificate, _localCertificate);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(handshakeBudget);

            QuicControlChannel control;
            try
            {
                control = await channel.AcceptControlChannelAsync(handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return PairingOutcome.Failed("A master connected but never started the pairing handshake.");
            }
            catch (ChannelClosedException)
            {
                return PairingOutcome.Failed("The master disconnected before starting the pairing handshake.");
            }

            await using (control.ConfigureAwait(false))
            {
                if (!await QuicPairingHandshake
                        .RunAsAgentAsync(control, presharedKey, binding, handshake.Token, handshakeBudget)
                        .ConfigureAwait(false))
                {
                    return PairingOutcome.Failed(
                        "The master did not prove the pre-shared key. Check that the same key was entered " +
                        "on both machines.");
                }

                try
                {
                    _trustedMasters.Pin(deviceId, thumbprint);
                }
                catch (PinConflictException conflict)
                {
                    await RefuseAsync(control, handshake.Token).ConfigureAwait(false);
                    return PairingOutcome.Failed(conflict.Message);
                }

                return PairingOutcome.Succeeded(deviceId, thumbprint);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PairingOutcome.Failed("The pairing handshake did not complete in time.");
        }
        finally
        {
            trust.Close();
            Interlocked.CompareExchange(ref _pairingWindow, null, trust);

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            else if (trust.Claimed.IsCompletedSuccessfully)
            {
                // Claimed in the instant the wait gave up: nobody else will ever dispose it.
                await trust.Claimed.Result.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tells a master that was verified that the pairing was nonetheless not recorded, so it
    /// does not pin an agent that did not pin it back. Best effort: the stream is aborted
    /// afterwards regardless, which the master also reads as a refusal.
    /// </summary>
    private static async Task RefuseAsync(QuicControlChannel control, CancellationToken ct)
    {
        try
        {
            await control.SendAsync(PairAck.Rejected, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }

        control.Abort(QuicControlChannel.PairingFailedErrorCode);
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is null)
        {
            throw new InvalidOperationException("The listener has not been started.");
        }
    }

    private async Task RunPumpAsync(QuicAcceptPump pump, CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            await pump.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Not swallowed: handed to every AcceptAsync caller through the queue's completion.
            fault = failure;
        }
        finally
        {
            _established.Writer.TryComplete(fault);
        }
    }

    /// <summary>
    /// Routes one established connection. A pinned master goes to <see cref="AcceptAsync"/>. An
    /// unpinned one can only have got through TLS on a pairing window, so it goes to that
    /// window's operation — and if the window is gone by now, nowhere: it is closed.
    /// </summary>
    private async ValueTask DeliverAsync(QuicConnection connection, CancellationToken ct)
    {
        string? deviceId = null;
        string? thumbprint = null;
        if (connection.RemoteCertificate is { } certificate)
        {
            thumbprint = PeerCertificateStore.ComputeThumbprint(certificate);
            _trustedMasters.TryGetDeviceIdForThumbprint(thumbprint, out deviceId);
        }

        if (deviceId is null)
        {
            if (thumbprint is null || Volatile.Read(ref _pairingWindow)?.TryClaim(connection, thumbprint) != true)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        var channel = new QuicChannel(connection, deviceId);
        try
        {
            await _established.Writer.WriteAsync(channel, ct).ConfigureAwait(false);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private QuicServerConnectionOptions CreateConnectionOptions() => new()
    {
        DefaultStreamErrorCode = 0,
        DefaultCloseErrorCode = 0,

        // Liveness is QUIC's job: there is no heartbeat message in this protocol.
        IdleTimeout = QuicTransportSettings.IdleTimeout,
        KeepAliveInterval = QuicTransportSettings.KeepAliveInterval,
        HandshakeTimeout = QuicTransportSettings.HandshakeTimeout,

        // Video arrives as one unidirectional stream per frame, so the agent must allow a
        // healthy number of concurrent inbound unidirectional streams; at the limit the
        // master's frame sends stall inside OpenOutboundStreamAsync. Streams are short lived
        // (one frame each), so this is a concurrency bound, not a total. The control channel
        // needs only one bidirectional stream.
        MaxInboundBidirectionalStreams = QuicTransportSettings.MaxInboundBidirectionalStreams,
        MaxInboundUnidirectionalStreams = QuicTransportSettings.AgentMaxInboundUnidirectionalStreams,

        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = _localCertificate,
            ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
            ClientCertificateRequired = true,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => IsTrustedMaster(certificate),
        },
    };

    /// <summary>
    /// The pins first, so a master that is already paired is accepted without touching a pairing
    /// window; the window, if one is open, only for a certificate no pin knows.
    /// </summary>
    private bool IsTrustedMaster(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        if (_trustedMasters.TryGetDeviceIdForThumbprint(
                PeerCertificateStore.ComputeThumbprint(certificate), out _))
        {
            return true;
        }

        return Volatile.Read(ref _pairingWindow)?.TryAdmit(certificate) ?? false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop the pump first, so the listener going away under it is a cancellation rather
        // than a fault.
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _pairingWindow)?.Close();

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_pumpTask is not null)
        {
            await _pumpTask.ConfigureAwait(false);
        }

        _established.Writer.TryComplete();
        while (_established.Reader.TryRead(out var unclaimed))
        {
            await unclaimed.DisposeAsync().ConfigureAwait(false);
        }

        _pumpCts.Dispose();
    }
}
