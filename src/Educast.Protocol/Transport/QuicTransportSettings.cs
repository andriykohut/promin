using System.Net.Quic;
using System.Net.Security;

namespace Educast.Protocol.Transport;

/// <summary>
/// The QUIC tuning both roles share. Every value here is set explicitly rather than
/// inherited from a framework default, because these numbers are the protocol: there is
/// no application-level heartbeat message anywhere in Educast, so connection liveness is
/// entirely QUIC's keep-alive and idle timeout, and video delivery is entirely stream
/// credit.
/// </summary>
public static class QuicTransportSettings
{
    /// <summary>ALPN identifier. A peer speaking anything else is rejected during TLS.</summary>
    public static SslApplicationProtocol ApplicationProtocol { get; } = new("educast/1");

    /// <summary>
    /// PING cadence on an otherwise idle connection. Two seconds costs a few dozen bytes a
    /// second on a LAN and keeps <see cref="IdleTimeout"/> from firing on a healthy but quiet
    /// link (between broadcasts, the control stream is silent for minutes at a time).
    /// </summary>
    public static TimeSpan KeepAliveInterval { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long silence is tolerated before QUIC declares the peer gone. Ten seconds is five
    /// missed keep-alives, so a burst of Wi-Fi loss does not tear down a working session, and a
    /// machine that is switched off or unplugged is detected within ten seconds — which is what
    /// makes "the master notices the client dropped" and "the agent tears its overlay down after
    /// a grace period" real behaviour rather than an aspiration. QUIC negotiates the smaller of
    /// the two peers' values, so both sides set the same number.
    /// </summary>
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Bound on the TLS handshake itself, so a half-open TCP-style stall fails fast.</summary>
    public static TimeSpan HandshakeTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Control messages need exactly one bidirectional stream. Four leaves room for a
    /// reconnect whose replacement control stream overlaps the old one briefly (Task 14)
    /// without ever granting enough credit for a peer to open control streams as a DoS.
    /// </summary>
    public const int MaxInboundBidirectionalStreams = 4;

    /// <summary>
    /// Video is one unidirectional stream per encoded frame: ~15 stream opens per second on
    /// each connection at the 15fps target. Credit is only released once a stream is closed
    /// and acknowledged, so a receiver that stalls for a moment must not starve the sender —
    /// at the limit the sender blocks inside <c>OpenOutboundStreamAsync</c> and frames queue
    /// behind each other, which is precisely the head-of-line blocking per-frame streams
    /// exist to avoid. 256 is roughly 17 seconds of credit at 15fps, far longer than any
    /// stall worth riding out (past that the frames are stale anyway and Task 9 drops them),
    /// while staying a hard bound: credit is a counter, not a preallocation.
    /// </summary>
    public const int MaxInboundUnidirectionalStreams = 256;

    /// <summary>
    /// Throws when QUIC is unavailable, with a message that names the actual cause rather
    /// than surfacing as an opaque <see cref="PlatformNotSupportedException"/> from deep in
    /// the stack.
    /// </summary>
    public static void EnsureSupported()
    {
        if (!QuicConnection.IsSupported || !QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "QUIC is not available on this machine. Educast requires msquic, which ships with the " +
                ".NET runtime on Windows 11; QuicConnection.IsSupported and QuicListener.IsSupported must both be true.");
        }
    }
}

/// <summary>How a peer's certificate is judged during the TLS handshake.</summary>
public enum PeerTrustMode
{
    /// <summary>
    /// Normal operation: the peer's certificate must match a pin already held for it.
    /// An unknown or re-keyed peer is refused at the TLS layer and never reaches the protocol.
    /// </summary>
    RequirePinned,

    /// <summary>
    /// Pairing mode only: accept the certificate the peer presents so the pre-shared-key
    /// exchange can run over it, then pin it (<see cref="QuicChannel.RemoteCertificateThumbprint"/>)
    /// if and only if pairing succeeded. This is the "first use" of trust-on-first-use and must
    /// never be left on outside an operator-initiated pairing window.
    /// </summary>
    TrustOnFirstUse,
}
