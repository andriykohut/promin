using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;

namespace Educast.Protocol.Transport;

/// <summary>
/// Runs the pre-shared-key pairing exchange over a live control channel under an explicit
/// deadline.
/// </summary>
/// <remarks>
/// <see cref="PairingService"/> enforces no deadline of its own — it relies entirely on the
/// caller's cancellation token — and the protocol has a shape that needs one: a prover that
/// receives an unexpected first message answers <c>false</c> without telling the verifier
/// anything, leaving the verifier blocked on a receive that will never complete. A peer that
/// connects and then says nothing would otherwise hang a pairing attempt forever. Every entry
/// point here therefore imposes a bounded overall deadline, and a deadline that expires is a
/// failed pairing (<c>false</c>), not an exception — an unauthenticated peer must never be able
/// to make this throw.
///
/// Roles are fixed by the transport, not by preference: a QUIC stream is invisible to the peer
/// until its opener writes, so the side that opened the control stream must speak first. The
/// master opens it, so the master is the verifier and the agent is the prover.
/// </remarks>
public static class QuicPairingHandshake
{
    /// <summary>
    /// Overall budget for the three-message exchange. Generous on a flat LAN, where the round
    /// trip is sub-millisecond, while still bounding a peer that stalls mid-handshake.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Master side: generates the nonce and sends the challenge first.</summary>
    public static Task<bool> RunAsMasterAsync(
        IControlChannel control,
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        RunAsync(new PairingService().AuthenticateAsMasterAsync, control, presharedKey, timeout, ct);

    /// <summary>Agent side: waits for the challenge, answers it, and awaits the acknowledgement.</summary>
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

        try
        {
            return await role(control, presharedKey, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The peer stalled. A pairing attempt that ran out of time simply did not pair.
            return false;
        }
        catch (ControlMessageFormatException)
        {
            // The peer is unauthenticated by definition at this point and may be hostile or
            // simply broken. Corrupt framing is a failed pairing, not an exception to propagate.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The peer closed the control stream mid-handshake.
            return false;
        }
    }
}
