using System.Security.Cryptography;
using Educast.Protocol.Messages;

namespace Educast.Protocol.Pairing;

/// <summary>
/// Pre-shared-key challenge-response pairing, run once per device before certificate
/// pinning takes over trust. Two roles:
///
/// - <b>Verifier</b>: generates the nonce and sends the challenge first. This must be the
///   side that opened the underlying transport stream/connection — on real QUIC, an
///   opened outbound stream is invisible to the peer until the opener writes to it, so
///   the opener cannot wait to receive first without deadlocking. In this system, the
///   master opens the control stream, so the master is always the verifier.
/// - <b>Prover</b>: waits for the challenge, computes the HMAC response, and answers it.
///   The agent is always the prover.
///
/// Neither side may throw on peer-controlled input: the peer is unauthenticated by
/// definition at this point in the protocol and may be hostile or simply corrupt. Any
/// unexpected message type or bad response is a clean `false`, never an exception.
/// </summary>
public sealed class PairingService
{
    public async Task<bool> AuthenticateAsVerifierAsync(IControlChannel channel, byte[] presharedKey, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await channel.SendAsync(new PairChallengeRequest(nonce), ct);

        var reply = await channel.ReceiveAsync(ct);
        if (reply is not PairChallengeResponse response)
        {
            await channel.SendAsync(new PairAck(Success: false), ct);
            return false;
        }

        var success = PairingCrypto.VerifyResponse(presharedKey, nonce, response.Hmac);
        await channel.SendAsync(new PairAck(success), ct);
        return success;
    }

    public async Task<bool> AuthenticateAsProverAsync(IControlChannel channel, byte[] presharedKey, CancellationToken ct)
    {
        var challenge = await channel.ReceiveAsync(ct);
        if (challenge is not PairChallengeRequest request)
        {
            return false;
        }

        var response = PairingCrypto.ComputeResponse(presharedKey, request.Nonce);
        await channel.SendAsync(new PairChallengeResponse(response), ct);

        var ack = await channel.ReceiveAsync(ct);
        return ack is PairAck { Success: true };
    }
}
