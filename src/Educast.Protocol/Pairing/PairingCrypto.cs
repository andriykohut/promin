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
/// Both proofs cover both nonces <b>and both certificates</b>, and are domain-separated by a
/// distinct label:
/// <code>
/// binding     = SHA256(masterCertDER) || SHA256(agentCertDER)
/// agentProof  = HMAC-SHA256(PSK, "educast-pair-agent\0"  || masterNonce || agentNonce || binding)
/// masterProof = HMAC-SHA256(PSK, "educast-pair-master\0" || masterNonce || agentNonce || binding)
/// </code>
/// <b>Domain separation</b> stops the reflection attack: with one key and a bare
/// <c>HMAC(PSK, nonce)</c> in both directions, a rogue master relays the agent's own nonce back
/// as its challenge and uses the agent as an oracle to produce the proof it owes.
///
/// <b>Both nonces in both proofs</b> bind the two messages to one handshake, so neither half
/// can be replayed into a different session.
///
/// <b>The binding</b> stops the relay. What pairing produces is a pin on a certificate, so the
/// proofs have to say which certificates they are about. Without it, a machine sitting between
/// the two ends with certificates of its own can forward all four messages unchanged: both real
/// machines know the key, both proofs verify, and each end pins <i>the relay</i>. With it, each
/// side hashes its own certificate and the one it actually received on its TLS connection; in
/// the presence of a relay those are different certificates at the two ends, and neither proof
/// verifies. This type treats the binding as opaque bytes — the transport computes it.
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
    public static byte[] ComputeAgentProof(
        byte[] presharedKey, byte[] masterNonce, byte[] agentNonce, ReadOnlySpan<byte> certificateBinding) =>
        Compute(AgentProofLabel, presharedKey, masterNonce, agentNonce, certificateBinding);

    /// <summary>The proof the master owes the agent.</summary>
    public static byte[] ComputeMasterProof(
        byte[] presharedKey, byte[] masterNonce, byte[] agentNonce, ReadOnlySpan<byte> certificateBinding) =>
        Compute(MasterProofLabel, presharedKey, masterNonce, agentNonce, certificateBinding);

    /// <summary>Constant-time check of the agent's proof. A null or short proof is simply false.</summary>
    public static bool VerifyAgentProof(
        byte[] presharedKey,
        byte[] masterNonce,
        byte[] agentNonce,
        ReadOnlySpan<byte> certificateBinding,
        byte[]? proof) =>
        Verify(AgentProofLabel, presharedKey, masterNonce, agentNonce, certificateBinding, proof);

    /// <summary>Constant-time check of the master's proof. A null or short proof is simply false.</summary>
    public static bool VerifyMasterProof(
        byte[] presharedKey,
        byte[] masterNonce,
        byte[] agentNonce,
        ReadOnlySpan<byte> certificateBinding,
        byte[]? proof) =>
        Verify(MasterProofLabel, presharedKey, masterNonce, agentNonce, certificateBinding, proof);

    private static bool Verify(
        byte[] label,
        byte[] presharedKey,
        byte[] masterNonce,
        byte[] agentNonce,
        ReadOnlySpan<byte> certificateBinding,
        byte[]? proof) =>
        proof is not null
        && CryptographicOperations.FixedTimeEquals(
            Compute(label, presharedKey, masterNonce, agentNonce, certificateBinding), proof);

    private static byte[] Compute(
        byte[] label,
        byte[] presharedKey,
        byte[] masterNonce,
        byte[] agentNonce,
        ReadOnlySpan<byte> certificateBinding)
    {
        ArgumentNullException.ThrowIfNull(presharedKey);
        ArgumentNullException.ThrowIfNull(masterNonce);
        ArgumentNullException.ThrowIfNull(agentNonce);

        var input = new byte[label.Length + masterNonce.Length + agentNonce.Length + certificateBinding.Length];
        label.CopyTo(input, 0);
        masterNonce.CopyTo(input, label.Length);
        agentNonce.CopyTo(input, label.Length + masterNonce.Length);
        certificateBinding.CopyTo(input.AsSpan(label.Length + masterNonce.Length + agentNonce.Length));

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
