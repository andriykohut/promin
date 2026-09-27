using System.Security.Cryptography;
using System.Text;

namespace Educast.Protocol.Pairing;

/// <summary>
/// The two proofs of the mutual pre-shared-key handshake.
/// </summary>
/// <remarks>
/// Pairing authenticates <b>both</b> directions. The agent proves it knows the pre-shared key,
/// and the master proves it too — before the agent pins the master's certificate as permanently
/// trusted. A one-directional handshake (the agent answering a challenge and trusting whatever
/// answered it) hands every student PC a chosen-nonce HMAC oracle over a human-typed key, and
/// lets any machine on the LAN be pinned as a trusted master during a pairing window. For a
/// product whose agent renders fullscreen whatever the master sends, that is the whole attack.
///
/// Both proofs cover <b>both</b> nonces and are domain-separated by a distinct label:
/// <code>
/// agentProof  = HMAC-SHA256(PSK, "educast-pair-agent\0"  || masterNonce || agentNonce)
/// masterProof = HMAC-SHA256(PSK, "educast-pair-master\0" || masterNonce || agentNonce)
/// </code>
/// Domain separation stops the reflection attack: with one key and a bare
/// <c>HMAC(PSK, nonce)</c> in both directions, a rogue master relays the agent's own nonce back
/// as its challenge and uses the agent as an oracle to produce the proof it owes. Covering both
/// nonces in both proofs also binds the two messages to one handshake, so neither half can be
/// replayed into a different session.
/// </remarks>
public static class PairingCrypto
{
    /// <summary>Length of each side's nonce. A peer offering any other length is refused.</summary>
    public const int NonceLength = 32;

    /// <summary>Length of either proof (HMAC-SHA256 output).</summary>
    public const int ProofLength = 32;

    private static readonly byte[] AgentProofLabel = Label("educast-pair-agent");
    private static readonly byte[] MasterProofLabel = Label("educast-pair-master");

    /// <summary>A fresh nonce for one handshake. Never reused.</summary>
    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary>A nonce is only usable at exactly <see cref="NonceLength"/> bytes.</summary>
    public static bool IsWellFormedNonce(byte[]? nonce) => nonce is { Length: NonceLength };

    /// <summary>The proof the agent owes the master.</summary>
    public static byte[] ComputeAgentProof(byte[] presharedKey, byte[] masterNonce, byte[] agentNonce) =>
        Compute(AgentProofLabel, presharedKey, masterNonce, agentNonce);

    /// <summary>The proof the master owes the agent.</summary>
    public static byte[] ComputeMasterProof(byte[] presharedKey, byte[] masterNonce, byte[] agentNonce) =>
        Compute(MasterProofLabel, presharedKey, masterNonce, agentNonce);

    /// <summary>Constant-time check of the agent's proof. A null or short proof is simply false.</summary>
    public static bool VerifyAgentProof(byte[] presharedKey, byte[] masterNonce, byte[] agentNonce, byte[]? proof) =>
        Verify(AgentProofLabel, presharedKey, masterNonce, agentNonce, proof);

    /// <summary>Constant-time check of the master's proof. A null or short proof is simply false.</summary>
    public static bool VerifyMasterProof(byte[] presharedKey, byte[] masterNonce, byte[] agentNonce, byte[]? proof) =>
        Verify(MasterProofLabel, presharedKey, masterNonce, agentNonce, proof);

    private static bool Verify(
        byte[] label, byte[] presharedKey, byte[] masterNonce, byte[] agentNonce, byte[]? proof) =>
        proof is not null
        && CryptographicOperations.FixedTimeEquals(
            Compute(label, presharedKey, masterNonce, agentNonce), proof);

    private static byte[] Compute(byte[] label, byte[] presharedKey, byte[] masterNonce, byte[] agentNonce)
    {
        ArgumentNullException.ThrowIfNull(presharedKey);
        ArgumentNullException.ThrowIfNull(masterNonce);
        ArgumentNullException.ThrowIfNull(agentNonce);

        var input = new byte[label.Length + masterNonce.Length + agentNonce.Length];
        label.CopyTo(input, 0);
        masterNonce.CopyTo(input, label.Length);
        agentNonce.CopyTo(input, label.Length + masterNonce.Length);

        return HMACSHA256.HashData(presharedKey, input);
    }

    /// <summary>The label plus its NUL terminator, which is what makes it unambiguously a prefix.</summary>
    private static byte[] Label(string text)
    {
        var bytes = new byte[text.Length + 1];
        Encoding.ASCII.GetBytes(text, bytes);
        return bytes;
    }
}
