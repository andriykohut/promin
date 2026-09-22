using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Educast.Protocol.Transport;

/// <summary>
/// The master (teacher machine) side of the transport: dials one agent and returns the
/// <see cref="QuicChannel"/> for it.
/// </summary>
/// <remarks>
/// The master always knows which agent it is dialling, so the agent certificate must match the
/// pin held for that exact device id — a certificate pinned for one agent never authenticates
/// another. In <see cref="PeerTrustMode.TrustOnFirstUse"/> — an operator-initiated pairing
/// window only — the presented certificate is accepted so the pre-shared-key exchange can run
/// over it, and the caller pins
/// <see cref="QuicChannel.RemoteCertificateThumbprint"/> afterwards if and only if pairing
/// succeeded.
/// </remarks>
public sealed class QuicMasterConnector
{
    private readonly X509Certificate2 _localCertificate;
    private readonly PeerCertificateStore _trustedAgents;
    private readonly PeerTrustMode _trustMode;

    public QuicMasterConnector(
        X509Certificate2 localCertificate,
        PeerCertificateStore trustedAgents,
        PeerTrustMode trustMode = PeerTrustMode.RequirePinned)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(trustedAgents);

        _localCertificate = localCertificate;
        _trustedAgents = trustedAgents;
        _trustMode = trustMode;
    }

    public async Task<QuicChannel> ConnectAsync(IPEndPoint endpoint, string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        QuicTransportSettings.EnsureSupported();

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = endpoint,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,

            // Liveness is QUIC's job: there is no heartbeat message in this protocol. QUIC
            // negotiates the smaller of the two peers' idle timeouts, so both sides set the same
            // value deliberately rather than inheriting a default.
            IdleTimeout = QuicTransportSettings.IdleTimeout,
            KeepAliveInterval = QuicTransportSettings.KeepAliveInterval,
            HandshakeTimeout = QuicTransportSettings.HandshakeTimeout,

            // The master receives control messages, not video, so it grants the agent enough
            // credit for the control stream plus reconnect overlap. Unidirectional credit is
            // kept symmetric with the agent so the direction of video is a protocol decision
            // rather than something transport limits impose.
            MaxInboundBidirectionalStreams = QuicTransportSettings.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = QuicTransportSettings.MaxInboundUnidirectionalStreams,

            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Address.ToString(),
                ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                ClientCertificates = [_localCertificate],
                RemoteCertificateValidationCallback = (_, certificate, _, _) => IsTrustedAgent(certificate, deviceId),
            },
        };

        var connection = await QuicConnection.ConnectAsync(options, ct).ConfigureAwait(false);
        return new QuicChannel(connection, _trustMode == PeerTrustMode.RequirePinned ? deviceId : null);
    }

    private bool IsTrustedAgent(X509Certificate? certificate, string deviceId)
    {
        if (certificate is null)
        {
            return false;
        }

        if (_trustMode == PeerTrustMode.TrustOnFirstUse)
        {
            return true;
        }

        var presented = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        return _trustedAgents.TryGetPinnedThumbprint(deviceId, out var pinned)
            && string.Equals(pinned, presented, StringComparison.OrdinalIgnoreCase);
    }
}
