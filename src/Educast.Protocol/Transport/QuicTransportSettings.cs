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
    /// How long silence is tolerated before QUIC declares the peer gone: 25 seconds, a dozen
    /// missed keep-alives. The number is set by the master, not by the link. The master is a
    /// dual-core machine doing capture, encode and twenty concurrent senders; one long GC
    /// pause, thermal stall or swap storm silences <i>every</i> connection at once, and a
    /// timeout shorter than that stall drops the whole class together and forces a full
    /// reconnect mid-broadcast. The cost is that a genuinely dead client — switched off,
    /// unplugged — is noticed up to 25 seconds after it went, rather than 10. QUIC negotiates
    /// the smaller of the two peers' values, so both sides set the same number. This value is
    /// reasoned, not measured: Task 15 measures the real one.
    /// </summary>
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(25);

    /// <summary>Bound on the TLS handshake itself, so a half-open TCP-style stall fails fast.</summary>
    public static TimeSpan HandshakeTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Budget for one whole master-side pairing operation: connect, mutual handshake, pin. This
    /// is also the lifetime of the trust-on-first-use acceptance the operation is allowed.
    /// </summary>
    public static TimeSpan PairingTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long an agent's pairing window stays open waiting for the master to dial, unless the
    /// caller asks for a different length. The window closes the moment a master is admitted,
    /// whether or not that pairing then succeeds.
    /// </summary>
    public static TimeSpan DefaultPairingWindow { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many inbound handshakes — completed or refused — the listener holds until the accept
    /// loop takes them. Stated rather than inherited because the accept loop's spin guard is
    /// sized against it: a full backlog of refused dials is consumed in at most three guard
    /// intervals (300 ms), so the guard can never hold a legitimate master back for long.
    /// </summary>
    public const int ListenBacklog = 512;

    /// <summary>
    /// Control messages need exactly one bidirectional stream. Four leaves room for a
    /// reconnect whose replacement control stream overlaps the old one briefly (Task 14)
    /// without ever granting enough credit for a peer to open control streams as a DoS.
    /// </summary>
    public const int MaxInboundBidirectionalStreams = 4;

    /// <summary>
    /// Inbound video credit the <b>agent</b> grants the master. Video is one unidirectional
    /// stream per encoded frame: ~15 stream opens per second on each connection at the 15fps
    /// target. Credit is only released once a stream is closed and acknowledged, so a receiver
    /// that stalls for a moment must not starve the sender — at the limit the sender blocks
    /// inside <c>OpenOutboundStreamAsync</c> and frames queue behind each other, which is
    /// precisely the head-of-line blocking per-frame streams exist to avoid. 256 is roughly 17
    /// seconds of credit at 15fps, far longer than any stall worth riding out (past that the
    /// frames are stale anyway and Task 9 drops them). Credit bounds how many streams may be
    /// open; <see cref="MaxConcurrentInboundVideoStreams"/> bounds how many are buffered.
    /// </summary>
    public const int AgentMaxInboundUnidirectionalStreams = 256;

    /// <summary>
    /// Inbound video credit the <b>master</b> grants an agent: none. Agents send no video, and
    /// agent machines are student-controlled — every stream of credit is up to
    /// <see cref="QuicChannel.MaxVideoFrameBytes"/> that a student's PC can make the teacher's
    /// laptop allocate, per connection, times twenty connections. If a later sub-project ever
    /// needs agent-to-master streams, this is the one constant to change.
    /// </summary>
    public const int MasterMaxInboundUnidirectionalStreams = 0;

    /// <summary>
    /// How many inbound video streams one connection reads into memory at once. Streams beyond
    /// this wait their turn unread (QUIC flow control holds the sender), so the most a peer can
    /// make the receiver buffer is this many frames of <see cref="QuicChannel.MaxVideoFrameBytes"/>
    /// — 32 MB — rather than one frame per stream of credit. Eight matches the inbound frame
    /// queue: more frames than that in flight are frames the consumer would drop anyway.
    /// </summary>
    public const int MaxConcurrentInboundVideoStreams = 8;

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
