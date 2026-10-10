using System.Security.Cryptography;
using Promin.Protocol.Messages;

namespace Promin.Protocol.Pairing;

/// <summary>
/// Mutual room-code pairing, run once per device before certificate pinning takes over trust.
/// A password-authenticated key exchange (J-PAKE) over the room code comes first, and its output
/// keys the proofs that follow:
/// <code>
/// master -> PairJPakeRound1
/// agent  -> PairJPakeRound1
/// master -> PairJPakeRound2
/// agent  -> PairJPakeRound2
///           both: sessionKey = J-PAKE output
/// master -> PairChallengeRequest(masterNonce)
/// agent  -> PairChallengeResponse(agentProof, agentNonce)
/// master -> PairAck(masterProof)
/// agent  -> verifies masterProof, and only then pins
/// </code>
/// </summary>
/// <remarks>
/// <b>Why J-PAKE.</b> The room code is six digits. Proofs keyed directly by it would let anyone
/// admitted by a pairing window keep one proof over known inputs and guess the code offline.
/// J-PAKE gives a stranger nothing to check a guess against: a wrong code does not fail inside
/// the exchange, it yields a different session key, and the first proof that fails is the single
/// guess the pairing window allows. The room code is used for nothing but J-PAKE; every proof
/// below is keyed by the session key.
///
/// <b>Both</b> sides prove knowledge of the session key (and so of the code). The earlier
/// one-directional version had the agent answer a challenge and then trust whatever said
/// "success": a rogue machine on the LAN could use an agent's pairing window as a chosen-nonce
/// HMAC oracle against a human-typed key and then have itself pinned as a permanently trusted
/// master. The proofs are domain-separated, cover both nonces, and cover a certificate binding —
/// see <see cref="PairingCrypto"/> for why.
///
/// <b>The certificate binding is supplied by the caller and is not optional.</b> This type does
/// not know what a certificate is and treats the binding as opaque bytes; what it guarantees is
/// that both proofs cover it, so that pairing only succeeds between two parties that passed in
/// the same bytes. The transport passes a digest of the two certificates at the ends of its own
/// TLS connection, which is what makes a relay in the middle fail. J-PAKE itself is not bound to
/// the certificates: that is deliberate, and it is the proofs that do it.
///
/// <b>These messages establish that both sides know the code; they do not, by themselves,
/// record anything.</b> Recording is the caller's step, and the transport follows it with a
/// further message of its own — the agent's explicit <see cref="PairConfirm"/> — so that neither
/// side ever has to infer from a closed stream or an expired wait that the other one finished.
///
/// <b>Roles are fixed by the transport, not by preference.</b> The master is the side that opens
/// the control stream, and an outbound QUIC stream is invisible to the peer until its opener
/// writes to it, so the master must speak first. The agent waits to receive.
///
/// <b>Neither side may throw on peer-controlled input.</b> The peer is unauthenticated by
/// definition at this point and may be hostile or simply corrupt: an unexpected message type, a
/// malformed J-PAKE round, a malformed nonce, a bad proof and a missing proof are all a clean
/// <c>false</c>.
///
/// <b>Neither side may leave the peer blocked.</b> A side that gives up sends
/// <see cref="PairAck.Rejected"/> first, so the peer fails immediately instead of sitting on a
/// receive until its deadline expires. That notification is best-effort: if it cannot be
/// delivered the result is still a clean <c>false</c>.
///
/// <b>The session key is secret.</b> It is cleared as soon as the proofs are done.
/// </remarks>
public sealed class PairingService
{
    /// <summary>
    /// Master side. Runs J-PAKE over the room code, challenges the agent, verifies the agent's
    /// proof, and then proves the session key to the agent in turn. Returns true only once its
    /// own proof has been handed over.
    /// </summary>
    /// <param name="roomCode">
    /// The room code's ASCII bytes. Used for nothing but J-PAKE; never keys a proof.
    /// </param>
    /// <param name="certificateBinding">
    /// Opaque bytes both proofs must cover; must be the same on both sides for pairing to
    /// succeed, and must not be empty.
    /// </param>
    public async Task<bool> AuthenticateAsMasterAsync(
        IControlChannel channel,
        byte[] roomCode,
        ReadOnlyMemory<byte> certificateBinding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(roomCode);
        ThrowIfNoBinding(certificateBinding);

        var exchange = new JPakeExchange(JPakeRole.Master, roomCode);

        await channel.SendAsync(exchange.CreateRound1(), ct).ConfigureAwait(false);
        if (!exchange.TryAcceptRound1(await channel.ReceiveAsync(ct).ConfigureAwait(false) as PairJPakeRound1))
        {
            await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
            return false;
        }

        await channel.SendAsync(exchange.CreateRound2(), ct).ConfigureAwait(false);
        if (!exchange.TryAcceptRound2(await channel.ReceiveAsync(ct).ConfigureAwait(false) as PairJPakeRound2))
        {
            await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
            return false;
        }

        var sessionKey = exchange.DeriveSessionKey();
        try
        {
            var masterNonce = PairingCrypto.CreateNonce();
            await channel.SendAsync(new PairChallengeRequest(masterNonce), ct).ConfigureAwait(false);

            var reply = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (reply is not PairChallengeResponse response
                || !PairingCrypto.IsWellFormedNonce(response.AgentNonce)
                || !PairingCrypto.VerifyAgentProof(
                    sessionKey, masterNonce, response.AgentNonce, certificateBinding.Span, response.AgentProof))
            {
                await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
                return false;
            }

            var masterProof = PairingCrypto.ComputeMasterProof(
                sessionKey, masterNonce, response.AgentNonce, certificateBinding.Span);
            await channel.SendAsync(new PairAck(Success: true, masterProof), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    /// <summary>
    /// Agent side. Runs J-PAKE over the room code, answers the challenge with a proof over both
    /// nonces and the binding, then requires the master's own proof before reporting success. The
    /// caller pins on that success and on nothing else.
    /// </summary>
    /// <param name="roomCode">
    /// The room code's ASCII bytes. Used for nothing but J-PAKE; never keys a proof.
    /// </param>
    /// <param name="certificateBinding">
    /// Opaque bytes both proofs must cover; must be the same on both sides for pairing to
    /// succeed, and must not be empty.
    /// </param>
    public async Task<bool> AuthenticateAsAgentAsync(
        IControlChannel channel,
        byte[] roomCode,
        ReadOnlyMemory<byte> certificateBinding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(roomCode);
        ThrowIfNoBinding(certificateBinding);

        var exchange = new JPakeExchange(JPakeRole.Agent, roomCode);

        // The agent listens first, but the underlying J-PAKE state machine insists that each of
        // its own rounds is created before the peer's matching round is validated. So each
        // round is created as soon as the peer's message is in hand, and sent only once the
        // peer's has checked out: nothing is revealed to a peer that has not earned it.
        var peerRound1 = await channel.ReceiveAsync(ct).ConfigureAwait(false) as PairJPakeRound1;
        var round1 = exchange.CreateRound1();
        if (!exchange.TryAcceptRound1(peerRound1))
        {
            // Signal rather than return silently: a master waiting on ReceiveAsync would
            // otherwise block until its deadline expired.
            await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
            return false;
        }

        await channel.SendAsync(round1, ct).ConfigureAwait(false);

        var peerRound2 = await channel.ReceiveAsync(ct).ConfigureAwait(false) as PairJPakeRound2;
        var round2 = exchange.CreateRound2();
        if (!exchange.TryAcceptRound2(peerRound2))
        {
            await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
            return false;
        }

        await channel.SendAsync(round2, ct).ConfigureAwait(false);
        var sessionKey = exchange.DeriveSessionKey();
        try
        {
            var challenge = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (challenge is not PairChallengeRequest request
                || !PairingCrypto.IsWellFormedNonce(request.MasterNonce))
            {
                await NotifyRejectedAsync(channel, ct).ConfigureAwait(false);
                return false;
            }

            var agentNonce = PairingCrypto.CreateNonce();
            var agentProof = PairingCrypto.ComputeAgentProof(
                sessionKey, request.MasterNonce, agentNonce, certificateBinding.Span);
            await channel.SendAsync(new PairChallengeResponse(agentProof, agentNonce), ct).ConfigureAwait(false);

            var ack = await channel.ReceiveAsync(ct).ConfigureAwait(false);

            // Success alone is a claim; the proof is the only thing worth pinning on.
            return ack is PairAck { Success: true } verdict
                && PairingCrypto.VerifyMasterProof(
                    sessionKey, request.MasterNonce, agentNonce, certificateBinding.Span, verdict.MasterProof);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
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
