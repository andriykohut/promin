using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
/// </remarks>
public sealed class QuicAgentListener : IAsyncDisposable
{
    private readonly X509Certificate2 _localCertificate;
    private readonly PeerCertificateStore _trustedMasters;
    private readonly int _port;
    private readonly PeerTrustMode _trustMode;
    private readonly IPAddress _bindAddress;
    private readonly Action<Exception>? _connectionRejected;
    private QuicListener? _listener;

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

        _listener = await QuicListener.ListenAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the next master connection that completed its handshake. The returned channel
    /// already knows which pinned device id the peer certificate belongs to (null while
    /// pairing), and owns the connection.
    /// </summary>
    /// <remarks>
    /// A peer whose certificate is refused does not fail quietly: .NET reports a failed
    /// handshake by throwing out of <c>AcceptConnectionAsync</c>, so one stranger dialling the
    /// port would otherwise kill the agent's accept loop and take the machine off the air until
    /// it is restarted. Handshake failures are therefore absorbed here — reported through the
    /// optional <c>connectionRejected</c> callback for the pairing UI to surface — and the
    /// listener goes back to waiting. Only cancellation and listener shutdown escape.
    /// </remarks>
    public async Task<QuicChannel> AcceptAsync(CancellationToken ct)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException("The listener has not been started.");
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            QuicConnection connection;
            try
            {
                connection = await _listener.AcceptConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (AuthenticationException rejected)
            {
                _connectionRejected?.Invoke(rejected);
                continue;
            }
            catch (QuicException rejected) when (rejected.QuicError != QuicError.OperationAborted)
            {
                _connectionRejected?.Invoke(rejected);
                continue;
            }

            string? deviceId = null;
            if (connection.RemoteCertificate is { } certificate)
            {
                _trustedMasters.TryGetDeviceIdForThumbprint(
                    certificate.GetCertHashString(HashAlgorithmName.SHA256),
                    out deviceId);
            }

            return new QuicChannel(connection, deviceId);
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

        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        return _trustedMasters.TryGetDeviceIdForThumbprint(thumbprint, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }
}
