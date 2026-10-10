using Promin.Protocol.Pairing;

namespace Promin.Protocol.Tests.Pairing;

/// <summary>
/// The two proofs of the mutual handshake. Both cover both nonces and the certificate binding,
/// and are domain-separated; these tests fail every shortcut that reintroduces the oracle, the
/// reflection attack, or the relay.
/// </summary>
public class PairingCryptoTests
{
    private static readonly byte[] Key = [9, 9, 9, 9];
    private static readonly byte[] MasterNonce = Enumerable.Range(0, PairingCrypto.NonceLength)
        .Select(i => (byte)i).ToArray();
    private static readonly byte[] AgentNonce = Enumerable.Range(0, PairingCrypto.NonceLength)
        .Select(i => (byte)(200 - i)).ToArray();

    /// <summary>Stands in for SHA256(masterCertDER) || SHA256(agentCertDER); opaque at this layer.</summary>
    private static readonly byte[] Binding = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray();

    [Fact]
    public void VerifyAgentProof_ReturnsTrue_WhenKeyNoncesAndBindingMatch()
    {
        var proof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce, Binding);

        Assert.True(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, Binding, proof));
    }

    [Fact]
    public void VerifyMasterProof_ReturnsTrue_WhenKeyNoncesAndBindingMatch()
    {
        var proof = PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce, Binding);

        Assert.True(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, Binding, proof));
    }

    [Fact]
    public void VerifyProofs_ReturnFalse_WhenTheKeyDoesNotMatch()
    {
        byte[] wrongKey = [1, 1, 1, 1];

        Assert.False(PairingCrypto.VerifyAgentProof(
            wrongKey, MasterNonce, AgentNonce, Binding,
            PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce, Binding)));
        Assert.False(PairingCrypto.VerifyMasterProof(
            wrongKey, MasterNonce, AgentNonce, Binding,
            PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce, Binding)));
    }

    [Fact]
    public void VerifyProofs_ReturnFalse_WhenTheProofIsMissingOrTheWrongLength()
    {
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, Binding, null));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, Binding, []));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, Binding, [1, 2, 3]));
        Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, Binding, null));
        Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, Binding, []));
    }

    /// <summary>
    /// The reflection attack: with one undifferentiated HMAC in both directions, a rogue master
    /// replays the agent's own nonce as its challenge and uses the agent as an oracle for the
    /// proof it owes. Domain separation is what makes that impossible — so the agent's proof must
    /// never verify as the master's, over identical inputs.
    /// </summary>
    [Fact]
    public void TheTwoProofs_AreDomainSeparated_OverIdenticalInputs()
    {
        var agentProof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce, Binding);
        var masterProof = PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce, Binding);

        Assert.False(agentProof.SequenceEqual(masterProof), "the two proofs are not domain-separated");
        Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, Binding, agentProof));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, Binding, masterProof));
    }

    /// <summary>
    /// A proof that covered only one nonce could be replayed into a different handshake. Fails an
    /// implementation that hashes just the master nonce, and one that hashes just the agent nonce.
    /// </summary>
    [Fact]
    public void EachProof_CoversBothNonces()
    {
        var proof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce, Binding);
        var otherNonce = PairingCrypto.CreateNonce();

        Assert.False(PairingCrypto.VerifyAgentProof(Key, otherNonce, AgentNonce, Binding, proof));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, otherNonce, Binding, proof));

        // Swapping the two nonces must change the proof, or their order carries no meaning.
        Assert.False(PairingCrypto.VerifyAgentProof(Key, AgentNonce, MasterNonce, Binding, proof));
    }

    /// <summary>
    /// A proof that did not cover the certificates would verify for whoever happened to be at the
    /// other end of the wire. Both proofs must change when either half of the binding changes —
    /// that is, when either end sees a different certificate. Fails proofs over the nonces alone,
    /// which is what lets a relay with its own certificates be pinned by both sides.
    /// </summary>
    [Fact]
    public void EachProof_CoversTheCertificateBinding()
    {
        var agentProof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce, Binding);
        var masterProof = PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce, Binding);

        var otherMasterHalf = (byte[])Binding.Clone();
        otherMasterHalf[0] ^= 0xFF;
        var otherAgentHalf = (byte[])Binding.Clone();
        otherAgentHalf[^1] ^= 0xFF;

        foreach (var different in new byte[][] { otherMasterHalf, otherAgentHalf, [] })
        {
            Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, different, agentProof));
            Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, different, masterProof));
        }
    }

    [Fact]
    public void CreateNonce_IsFreshAndTheDeclaredLength()
    {
        var first = PairingCrypto.CreateNonce();
        var second = PairingCrypto.CreateNonce();

        Assert.Equal(PairingCrypto.NonceLength, first.Length);
        Assert.False(first.SequenceEqual(second), "CreateNonce repeated itself");
    }

    [Fact]
    public void IsWellFormedNonce_AcceptsOnlyTheDeclaredLength()
    {
        Assert.True(PairingCrypto.IsWellFormedNonce(PairingCrypto.CreateNonce()));
        Assert.False(PairingCrypto.IsWellFormedNonce(null));
        Assert.False(PairingCrypto.IsWellFormedNonce([]));
        Assert.False(PairingCrypto.IsWellFormedNonce(new byte[PairingCrypto.NonceLength - 1]));
        Assert.False(PairingCrypto.IsWellFormedNonce(new byte[PairingCrypto.NonceLength + 1]));
    }

    /// <summary>
    /// The exact construction, pinned against an independent computation of it, so a later edit
    /// to a label, to the field order, or to whether the binding is included is caught rather
    /// than silently changing the protocol.
    /// </summary>
    [Fact]
    public void Proofs_MatchTheSpecifiedConstruction()
    {
        Assert.Equal(
            Expected("promin-pair-agent\0"),
            PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce, Binding));
        Assert.Equal(
            Expected("promin-pair-master\0"),
            PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce, Binding));

        static byte[] Expected(string label)
        {
            var input = System.Text.Encoding.ASCII.GetBytes(label)
                .Concat(MasterNonce).Concat(AgentNonce).Concat(Binding).ToArray();
            return System.Security.Cryptography.HMACSHA256.HashData(Key, input);
        }
    }
}
