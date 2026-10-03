using System.Net.Quic;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;

namespace Educast.Protocol.Transport;

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
/// failed pairing (<c>false</c>), not an exception.
///
/// <b>What becomes <c>false</c>.</b> Only what the unauthenticated peer can cause: stalling,
/// corrupt framing, closing or aborting the stream, dropping the connection. The peer must never
/// be able to make this throw.
///
/// <b>What does not.</b> Faults of this process. <see cref="ObjectDisposedException"/> (a
/// channel used after disposal), any other <see cref="InvalidOperationException"/>, and a QUIC
/// operation aborted locally all propagate. Reporting those as "pairing failed" hides a
/// lifecycle bug behind a message that sends someone looking at the network.
///
/// <b>A failed handshake does not leave the stream half-finished.</b> When the result is
/// <c>false</c> the control stream is aborted with
/// <see cref="QuicControlChannel.PairingFailedErrorCode"/>, so a peer still waiting on it fails
/// immediately. The owner disposes the channel as usual.
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
    public static Task<bool> RunAsMasterAsync(
        IControlChannel control,
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        RunAsync(new PairingService().AuthenticateAsMasterAsync, control, presharedKey, timeout, ct);

    /// <summary>Agent side: answers the challenge, then requires the master's proof.</summary>
    public static Task<bool> RunAsAgentAsync(
        IControlChannel control,
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        RunAsync(new PairingService().AuthenticateAsAgentAsync, control, presharedKey, timeout, ct);

    private static async Task<bool> RunAsync(
        Func<IControlChannel, byte[], CancellationToken, Task<bool>> role,
        IControlChannel control,
        byte[] presharedKey,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(presharedKey);

        var deadline = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(deadline);

        bool paired;
        try
        {
            paired = await role(control, presharedKey, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The peer stalled. A pairing attempt that ran out of time simply did not pair.
            paired = false;
        }
        catch (ControlMessageFormatException)
        {
            // Corrupt framing from a peer that is unauthenticated by definition.
            paired = false;
        }
        catch (ControlChannelClosedException)
        {
            // The peer closed the control stream mid-handshake.
            paired = false;
        }
        catch (QuicException peerFailure) when (peerFailure.QuicError != QuicError.OperationAborted)
        {
            // The peer aborted the stream or the connection went away. OperationAborted is
            // excluded on purpose: that one is this process tearing its own stream down.
            paired = false;
        }

        if (!paired && control is QuicControlChannel quicControl)
        {
            quicControl.Abort(QuicControlChannel.PairingFailedErrorCode);
        }

        return paired;
    }
}
