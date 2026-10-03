using Educast.Protocol.Messages;

namespace Educast.Protocol.Pairing;

/// <summary>
/// Mutual pre-shared-key pairing, run once per device before certificate pinning takes over
/// trust. Four messages:
/// <code>
/// master -> PairChallengeRequest(masterNonce)
/// agent  -> PairChallengeResponse(agentProof, agentNonce)
/// master -> PairAck(masterProof)
/// agent  -> verifies masterProof, and only then pins
/// </code>
/// </summary>
/// <remarks>
/// <b>Both</b> sides prove knowledge of the key. The earlier one-directional version had the
/// agent answer a challenge and then trust whatever said "success": a rogue machine on the LAN
/// could use an agent's pairing window as a chosen-nonce HMAC oracle against a human-typed key
/// and then have itself pinned as a permanently trusted master. The proofs are domain-separated,
/// cover both nonces, and cover a certificate binding — see <see cref="PairingCrypto"/> for why.
///
/// <b>The certificate binding is supplied by the caller and is not optional.</b> This type does
/// not know what a certificate is and treats the binding as opaque bytes; what it guarantees is
/// that both proofs cover it, so that pairing only succeeds between two parties that passed in
/// the same bytes. The transport passes a digest of the two certificates at the ends of its own
/// TLS connection, which is what makes a relay in the middle fail.
///
/// <b>These four messages establish that both sides know the key; they do not, by themselves,
/// record anything.</b> Recording is the caller's step, and the transport follows it with a
/// fifth message of its own — the agent's explicit <see cref="PairConfirm"/> — so that neither
/// side ever has to infer from a closed stream or an expired wait that the other one finished.
///
/// <b>Roles are fixed by the transport, not by preference.</b> The master is the side that opens
/// the control stream, and an outbound QUIC stream is invisible to the peer until its opener
/// writes to it, so the master must speak first. The agent waits to receive.
///
/// <b>Neither side may throw on peer-controlled input.</b> The peer is unauthenticated by
/// definition at this point and may be hostile or simply corrupt: an unexpected message type, a
/// malformed nonce, a bad proof and a missing proof are all a clean <c>false</c>.
///
/// <b>Neither side may leave the peer blocked.</b> A side that gives up sends
/// <see cref="PairAck.Rejected"/> first, so the peer fails immediately instead of sitting on a
/// receive until its deadline expires. That notification is best-effort: if it cannot be
/// delivered the result is still a clean <c>false</c>.
/// </remarks>
public sealed class PairingService
{
    /// <summary>
    /// Master side. Challenges the agent, verifies the agent's proof, and then proves the key
    /// to the agent in turn. Returns true only once its own proof has been handed over.
    /// </summary>
    /// <param name="certificateBinding">
    /// Opaque bytes both proofs must cover; must be the same on both sides for pairing to
    /// succeed, and must not be empty.
    /// </param>
    public async Task<bool> AuthenticateAsMasterAsync(
        IControlChannel channel,
        byte[] presharedKey,
        ReadOnlyMemory<byte> certificateBinding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(presharedKey);
        ThrowIfNoBinding(certificateBinding);

        var masterNonce = PairingCrypto.CreateNonce();
        await channel.SendAsync(new PairChallengeRequest(masterNonce), ct).ConfigureAwait(false);

        var reply = await channel.ReceiveAsync(ct).ConfigureAwait(false);
        if (reply is not PairChallengeResponse response
            || !PairingCrypto.IsWellFormedNonce(response.AgentNonce)
            || !PairingCrypto.VerifyAgentProof(
                presharedKey, masterNonce, response.AgentNonce, certificateBinding.Span, response.AgentProof))
        {
            await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
            return false;
        }

        var masterProof = PairingCrypto.ComputeMasterProof(
            presharedKey, masterNonce, response.AgentNonce, certificateBinding.Span);
        await channel.SendAsync(new PairAck(Success: true, masterProof), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Agent side. Answers the challenge with a proof over both nonces and the binding, then
    /// requires the master's own proof before reporting success. The caller pins on that success
    /// and on nothing else.
    /// </summary>
    /// <param name="certificateBinding">
    /// Opaque bytes both proofs must cover; must be the same on both sides for pairing to
    /// succeed, and must not be empty.
    /// </param>
    public async Task<bool> AuthenticateAsAgentAsync(
        IControlChannel channel,
        byte[] presharedKey,
        ReadOnlyMemory<byte> certificateBinding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(presharedKey);
        ThrowIfNoBinding(certificateBinding);

        var challenge = await channel.ReceiveAsync(ct).ConfigureAwait(false);
        if (challenge is not PairChallengeRequest request
            || !PairingCrypto.IsWellFormedNonce(request.MasterNonce))
        {
            // Signal rather than return silently: a master waiting on ReceiveAsync would
            // otherwise block until its deadline expired.
            await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
            return false;
        }

        var agentNonce = PairingCrypto.CreateNonce();
        var agentProof = PairingCrypto.ComputeAgentProof(
            presharedKey, request.MasterNonce, agentNonce, certificateBinding.Span);
        await channel.SendAsync(new PairChallengeResponse(agentProof, agentNonce), ct).ConfigureAwait(false);

        var ack = await channel.ReceiveAsync(ct).ConfigureAwait(false);

        // Success alone is a claim; the proof is the only thing worth pinning on.
        return ack is PairAck { Success: true } verdict
            && PairingCrypto.VerifyMasterProof(
                presharedKey, request.MasterNonce, agentNonce, certificateBinding.Span, verdict.MasterProof);
    }

    /// <summary>
    /// An empty binding would silently turn the relay protection off, so it is a programming
    /// error rather than a mode.
    /// </summary>
    private static void ThrowIfNoBinding(ReadOnlyMemory<byte> certificateBinding)
    {
        if (certificateBinding.IsEmpty)
        {
            throw new ArgumentException(
                "A certificate binding is required: without one the proofs say nothing about which " +
                "certificates are being paired.",
                nameof(certificateBinding));
        }
    }

    /// <summary>
    /// Best-effort "I am giving up" so the peer does not sit blocked on a receive. A failure to
    /// deliver it changes nothing: the pairing has already failed.
    /// </summary>
    private static async Task NotifyRejectedAsync(IControlChannel channel, CancellationToken ct)
    {
        try
        {
            await channel.SendAsync(PairAck.Rejected, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Out of time. Cancellation is the caller's to observe, never swallowed here.
            throw;
        }
        catch (Exception)
        {
            // The channel is already gone. Nothing to tell the peer, and nothing to report:
            // the caller is about to return a failed pairing regardless.
        }
    }
}
