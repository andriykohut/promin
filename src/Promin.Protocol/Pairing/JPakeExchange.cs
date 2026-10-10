using System.Security.Cryptography;
using Promin.Protocol.Messages;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Promin.Protocol.Pairing;

public enum JPakeRole
{
    Master,
    Agent,
}

/// <summary>
/// One side of a J-PAKE exchange (RFC 8236) over the room code. Rounds 1 and 2 only:
/// key confirmation is done by the certificate-bound proofs in <see cref="PairingCrypto"/>,
/// keyed by <see cref="DeriveSessionKey"/>, so that confirmation covers both certificates.
/// </summary>
/// <remarks>
/// <b>Call order matters, because BouncyCastle's state machine enforces it:</b> a side must
/// create its own round 1 before it accepts the peer's round 1, and create its own round 2 before
/// it accepts the peer's round 2. A side that speaks first (the master) does this naturally; a
/// side that listens first (the agent) must create its round when the peer's message arrives and
/// may still choose not to send it. Out-of-order calls throw <see cref="InvalidOperationException"/>:
/// they are a programming error, not something a peer can cause.
/// </remarks>
public sealed class JPakeExchange
{
    public const int SessionKeyLength = 32;

    /// <summary>Largest encoding of one group element or exponent: 3072 bits.</summary>
    public const int MaxElementLength = 384;

    private const string MasterId = "promin-master";
    private const string AgentId = "promin-agent";
    private static readonly byte[] SessionKeyInfo = "promin-pair-session-v1"u8.ToArray();

    private readonly JPakeParticipant _participant;
    private readonly string _peerId;

    public JPakeExchange(JPakeRole role, ReadOnlySpan<byte> roomCode)
    {
        if (roomCode.IsEmpty)
        {
            throw new ArgumentException("A room code is required.", nameof(roomCode));
        }

        var password = new char[roomCode.Length];
        for (var i = 0; i < roomCode.Length; i++)
        {
            password[i] = (char)roomCode[i];
        }

        _peerId = role == JPakeRole.Master ? AgentId : MasterId;
        _participant = new JPakeParticipant(
            role == JPakeRole.Master ? MasterId : AgentId,
            password,
            JPakePrimeOrderGroups.NIST_3072,
            new Sha256Digest(),
            new SecureRandom());
    }

    public PairJPakeRound1 CreateRound1()
    {
        var p = _participant.CreateRound1PayloadToSend();
        return new PairJPakeRound1(Bytes(p.Gx1), Bytes(p.Gx2), Bytes(p.KnowledgeProofForX1), Bytes(p.KnowledgeProofForX2));
    }

    /// <summary>False when the peer's round 1 is malformed or its proofs do not verify.</summary>
    public bool TryAcceptRound1(PairJPakeRound1? peer)
    {
        if (peer is null
            || !IsElement(peer.Gx1) || !IsElement(peer.Gx2)
            || !IsProof(peer.ProofX1) || !IsProof(peer.ProofX2))
        {
            return false;
        }

        try
        {
            _participant.ValidateRound1PayloadReceived(new JPakeRound1Payload(
                _peerId, Number(peer.Gx1), Number(peer.Gx2), Numbers(peer.ProofX1), Numbers(peer.ProofX2)));
            return true;
        }
        catch (CryptoException)
        {
            return false;
        }
    }

    public PairJPakeRound2 CreateRound2()
    {
        var p = _participant.CreateRound2PayloadToSend();
        return new PairJPakeRound2(Bytes(p.A), Bytes(p.KnowledgeProofForX2s));
    }

    /// <summary>False when the peer's round 2 is malformed or its proof does not verify.</summary>
    public bool TryAcceptRound2(PairJPakeRound2? peer)
    {
        if (peer is null || !IsElement(peer.A) || !IsProof(peer.ProofX2s))
        {
            return false;
        }

        try
        {
            _participant.ValidateRound2PayloadReceived(
                new JPakeRound2Payload(_peerId, Number(peer.A), Numbers(peer.ProofX2s)));
            return true;
        }
        catch (CryptoException)
        {
            return false;
        }
    }

    /// <summary>
    /// The 32-byte key both sides share only if they used the same room code. A wrong code
    /// does not fail here: it yields a different key, and the proofs keyed by it then fail.
    /// </summary>
    public byte[] DeriveSessionKey()
    {
        var keyingMaterial = _participant.CalculateKeyingMaterial().ToByteArrayUnsigned();
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, keyingMaterial, SessionKeyLength, salt: null, info: SessionKeyInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyingMaterial);
        }
    }

    private static bool IsElement(byte[]? value) => value is { Length: > 0 and <= MaxElementLength };

    private static bool IsProof(byte[][]? proof) =>
        proof is { Length: 2 } && IsElement(proof[0]) && IsElement(proof[1]);

    private static byte[] Bytes(BigInteger value) => value.ToByteArrayUnsigned();

    private static byte[][] Bytes(BigInteger[] values) => [.. values.Select(Bytes)];

    private static BigInteger Number(byte[] value) => new(1, value);

    private static BigInteger[] Numbers(byte[][] values) => [.. values.Select(Number)];
}
