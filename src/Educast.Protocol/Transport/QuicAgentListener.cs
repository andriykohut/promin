using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace Educast.Protocol.Transport;

/// <summary>
/// The agent (client machine) side of the transport: listens for the master to dial in and
/// hands back one <see cref="QuicChannel"/> per accepted connection.
/// </summary>
/// <remarks>
/// Masters are pinned by their real device id, like every other peer. The dialling master is
/// unknown until TLS presents its certificate, so the certificate thumbprint is resolved back
/// to a device id through <see cref="PeerCertificateStore.TryGetDeviceIdForThumbprint"/>; any
/// certificate that resolves to no pin is refused during the handshake and never reaches the
/// protocol. In <see cref="PeerTrustMode.TrustOnFirstUse"/> — an operator-initiated pairing
/// window only — the certificate is accepted so the pre-shared-key exchange can run over it,
/// and the caller pins it afterwards if and only if pairing succeeded.
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
    private readonly PeerTrustMode _trustMode;
    private readonly IPAddress _bindAddress;
    private readonly Action<Exception>? _connectionRejected;
    private readonly Channel<QuicChannel> _established = Channel.CreateBounded<QuicChannel>(
        new BoundedChannelOptions(AcceptQueueCapacity) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _pumpCts = new();
    private QuicListener? _listener;
    private QuicAcceptPump? _pump;
    private Task? _pumpTask;
    private int _disposed;

    public QuicAgentListener(
        X509Certificate2 localCertificate,
        PeerCertificateStore trustedMasters,
        int port,
        PeerTrustMode trustMode = PeerTrustMode.RequirePinned,
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
        _trustMode = trustMode;
        _bindAddress = bindAddress ?? IPAddress.Any;
        _connectionRejected = connectionRejected;
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
        _pumpTask = RunPumpAsync(_pump, _pumpCts.Token);
    }

    /// <summary>
    /// Returns the next master connection that completed its handshake. The returned channel
    /// already knows which pinned device id the peer certificate belongs to (null while
    /// pairing), and owns the connection.
    /// </summary>
    /// <remarks>
    /// A peer whose certificate is refused never appears here: the refusal is reported through
    /// the optional <c>connectionRejected</c> callback and the listener carries on. What does
    /// come out of this method, besides cancellation, is a fault of this machine's own — a
    /// credential that will not load, for instance. That is thrown to every caller from then
    /// on, because a listener in that state is broken, not busy.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The listener was disposed.</exception>
    public async Task<QuicChannel> AcceptAsync(CancellationToken ct)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException("The listener has not been started.");
        }

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

    private async ValueTask DeliverAsync(QuicConnection connection, CancellationToken ct)
    {
        string? deviceId = null;
        if (connection.RemoteCertificate is { } certificate)
        {
            _trustedMasters.TryGetDeviceIdForThumbprint(
                PeerCertificateStore.ComputeThumbprint(certificate), out deviceId);
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

    private bool IsTrustedMaster(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        if (_trustMode == PeerTrustMode.TrustOnFirstUse)
        {
            return true;
        }

        return _trustedMasters.TryGetDeviceIdForThumbprint(
            PeerCertificateStore.ComputeThumbprint(certificate), out _);
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
