using Educast.Protocol.Pairing;

namespace Educast.Protocol.Tests.Pairing;

/// <summary>
/// The two proofs of the mutual handshake. Both cover both nonces and are domain-separated;
/// these tests fail every shortcut that reintroduces the oracle or the reflection attack.
/// </summary>
public class PairingCryptoTests
{
    private static readonly byte[] Key = [9, 9, 9, 9];
    private static readonly byte[] MasterNonce = Enumerable.Range(0, PairingCrypto.NonceLength)
        .Select(i => (byte)i).ToArray();
    private static readonly byte[] AgentNonce = Enumerable.Range(0, PairingCrypto.NonceLength)
        .Select(i => (byte)(200 - i)).ToArray();

    [Fact]
    public void VerifyAgentProof_ReturnsTrue_WhenKeyAndBothNoncesMatch()
    {
        var proof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce);

        Assert.True(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, proof));
    }

    [Fact]
    public void VerifyMasterProof_ReturnsTrue_WhenKeyAndBothNoncesMatch()
    {
        var proof = PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce);

        Assert.True(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, proof));
    }

    [Fact]
    public void VerifyProofs_ReturnFalse_WhenTheKeyDoesNotMatch()
    {
        byte[] wrongKey = [1, 1, 1, 1];

        Assert.False(PairingCrypto.VerifyAgentProof(
            wrongKey, MasterNonce, AgentNonce, PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce)));
        Assert.False(PairingCrypto.VerifyMasterProof(
            wrongKey, MasterNonce, AgentNonce, PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce)));
    }

    [Fact]
    public void VerifyProofs_ReturnFalse_WhenTheProofIsMissingOrTheWrongLength()
    {
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, null));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, []));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, [1, 2, 3]));
        Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, null));
        Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, []));
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
        var agentProof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce);
        var masterProof = PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce);

        Assert.False(agentProof.SequenceEqual(masterProof), "the two proofs are not domain-separated");
        Assert.False(PairingCrypto.VerifyMasterProof(Key, MasterNonce, AgentNonce, agentProof));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, AgentNonce, masterProof));
    }

    /// <summary>
    /// A proof that covered only one nonce could be replayed into a different handshake. Fails an
    /// implementation that hashes just the master nonce, and one that hashes just the agent nonce.
    /// </summary>
    [Fact]
    public void EachProof_CoversBothNonces()
    {
        var proof = PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce);
        var otherNonce = PairingCrypto.CreateNonce();

        Assert.False(PairingCrypto.VerifyAgentProof(Key, otherNonce, AgentNonce, proof));
        Assert.False(PairingCrypto.VerifyAgentProof(Key, MasterNonce, otherNonce, proof));

        // Swapping the two nonces must change the proof, or their order carries no meaning.
        Assert.False(PairingCrypto.VerifyAgentProof(Key, AgentNonce, MasterNonce, proof));
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
    /// to the label or the field order is caught rather than silently changing the protocol.
    /// </summary>
    [Fact]
    public void Proofs_MatchTheSpecifiedConstruction()
    {
        Assert.Equal(
            Expected("educast-pair-agent\0"),
            PairingCrypto.ComputeAgentProof(Key, MasterNonce, AgentNonce));
        Assert.Equal(
            Expected("educast-pair-master\0"),
            PairingCrypto.ComputeMasterProof(Key, MasterNonce, AgentNonce));

        static byte[] Expected(string label)
        {
            var input = System.Text.Encoding.ASCII.GetBytes(label)
                .Concat(MasterNonce).Concat(AgentNonce).ToArray();
            return System.Security.Cryptography.HMACSHA256.HashData(Key, input);
        }
    }
}
