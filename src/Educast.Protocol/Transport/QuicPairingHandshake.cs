using System.Net.Quic;
using System.Security.Authentication;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;

namespace Educast.Protocol.Transport;

/// <summary>How one side's half of the handshake ended.</summary>
internal enum PairingHandshakeResult
{
    /// <summary>Both proofs verified.</summary>
    Paired,

    /// <summary>
    /// The exchange ran but a proof did not verify, or the peer said no. The keys differ, or
    /// the two ends are not looking at the same certificates.
    /// </summary>
    KeyNotProven,

    /// <summary>The peer refused this side's certificate (a TLS alert on the connection).</summary>
    PeerRefused,

    /// <summary>The peer closed or aborted the stream, or the connection went away.</summary>
    PeerClosed,

    /// <summary>The handshake's own deadline ran out.</summary>
    TimedOut,

    /// <summary>The peer's framing was corrupt.</summary>
    CorruptFrame,
}

/// <summary>
/// Runs the mutual pre-shared-key exchange over a live control channel under an explicit
/// deadline. Internal: the public entry points are the pairing operations
/// (<see cref="QuicMasterConnector.PairAsync"/> and
/// <see cref="QuicAgentListener.AcceptPairingAsync"/>), which run this and then pin — on
/// success and on nothing else. Running the handshake without the pinning step, or pinning
/// without it, is not something a caller outside the transport gets to do.
/// </summary>
/// <remarks>
/// <b>The deadline is this class's own.</b> <see cref="PairingService"/> enforces none — it
/// relies entirely on the token it is handed — and a peer that connects and then says nothing
/// would hang a pairing attempt forever. Every entry point here therefore imposes a bounded
/// overall deadline regardless of what token the caller passes, and a deadline that expires is a
/// failed pairing, not an exception.
///
/// <b>What becomes a result.</b> Only what the unauthenticated peer can cause: stalling, corrupt
/// framing, refusing this side's certificate, closing or aborting the stream, dropping the
/// connection. The peer must never be able to make this throw.
///
/// <b>What does not.</b> Faults of this process. <see cref="ObjectDisposedException"/> (a
/// channel used after disposal), any other <see cref="InvalidOperationException"/>, and a QUIC
/// operation aborted locally all propagate. Reporting those as "pairing failed" hides a
/// lifecycle bug behind a message that sends someone looking at the network.
///
/// <b>Anything short of success aborts the stream.</b> Whenever this does not return
/// <see cref="PairingHandshakeResult.Paired"/> — a failed result, the caller's own token firing,
/// or an exception on its way out — the control stream is aborted with
/// <see cref="QuicControlChannel.PairingFailedErrorCode"/> first. The peer never sees a stream
/// that merely closes after a handshake that did not succeed, and so can never mistake a
/// timeout on this side for an orderly finish.
///
/// Roles are fixed by the transport, not by preference: a QUIC stream is invisible to the peer
/// until its opener writes, so the side that opened the control stream must speak first. The
/// master opens it.
/// </remarks>
internal static class QuicPairingHandshake
{
    /// <summary>
    /// Overall budget for the exchange. Generous on a flat LAN, where a round trip is
    /// sub-millisecond, while still bounding a peer that stalls mid-handshake.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Master side: sends the challenge first, verifies the agent, then proves itself.</summary>
    public static Task<PairingHandshakeResult> RunAsMasterAsync(
        IControlChannel control,
        byte[] presharedKey,
        ReadOnlyMemory<byte> certificateBinding,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        RunAsync(
            new PairingService().AuthenticateAsMasterAsync, control, presharedKey, certificateBinding, timeout, ct);

    /// <summary>Agent side: answers the challenge, then requires the master's proof.</summary>
    public static Task<PairingHandshakeResult> RunAsAgentAsync(
        IControlChannel control,
        byte[] presharedKey,
        ReadOnlyMemory<byte> certificateBinding,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        RunAsync(
            new PairingService().AuthenticateAsAgentAsync, control, presharedKey, certificateBinding, timeout, ct);

    private static async Task<PairingHandshakeResult> RunAsync(
        Func<IControlChannel, byte[], ReadOnlyMemory<byte>, CancellationToken, Task<bool>> role,
        IControlChannel control,
        byte[] presharedKey,
        ReadOnlyMemory<byte> certificateBinding,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(presharedKey);

        var deadline = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(deadline);

        // Anything but Paired by the time control leaves this method — including by exception —
        // aborts the stream in the finally below.
        var result = PairingHandshakeResult.PeerClosed;
        try
        {
            try
            {
                result = await role(control, presharedKey, certificateBinding, linked.Token).ConfigureAwait(false)
                    ? PairingHandshakeResult.Paired
                    : PairingHandshakeResult.KeyNotProven;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The peer stalled. A pairing attempt that ran out of time simply did not pair.
                result = PairingHandshakeResult.TimedOut;
            }
            catch (ControlMessageFormatException)
            {
                // Corrupt framing from a peer that is unauthenticated by definition.
                result = PairingHandshakeResult.CorruptFrame;
            }
            catch (ControlChannelClosedException)
            {
                // The peer closed the control stream mid-handshake.
                result = PairingHandshakeResult.PeerClosed;
            }
            catch (AuthenticationException)
            {
                // The peer refused this side's certificate. TLS 1.3 delivers that verdict after
                // the connection is already up, so it lands on whatever operation is in flight.
                result = PairingHandshakeResult.PeerRefused;
            }
            catch (QuicException peerFailure) when (peerFailure.QuicError != QuicError.OperationAborted)
            {
                // The peer aborted the stream or the connection went away. OperationAborted is
                // excluded on purpose: that one is this process tearing its own stream down.
                result = PairingHandshakeResult.PeerClosed;
            }

            return result;
        }
        finally
        {
            if (result != PairingHandshakeResult.Paired && control is QuicControlChannel quicControl)
            {
                quicControl.Abort(QuicControlChannel.PairingFailedErrorCode);
            }
        }
    }
}
