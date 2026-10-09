using System.Text.Json.Serialization;

namespace Educast.Protocol.Messages;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PairJPakeRound1), "pairJPakeRound1")]
[JsonDerivedType(typeof(PairJPakeRound2), "pairJPakeRound2")]
[JsonDerivedType(typeof(PairChallengeRequest), "pairChallengeRequest")]
[JsonDerivedType(typeof(PairChallengeResponse), "pairChallengeResponse")]
[JsonDerivedType(typeof(PairAck), "pairAck")]
[JsonDerivedType(typeof(PairConfirm), "pairConfirm")]
[JsonDerivedType(typeof(StartBroadcast), "startBroadcast")]
[JsonDerivedType(typeof(StopBroadcast), "stopBroadcast")]
[JsonDerivedType(typeof(BroadcastReady), "broadcastReady")]
public abstract record ControlMessage;

/// <summary>
/// J-PAKE round 1 (RFC 8236), sent by each side: two public group elements and a Schnorr
/// proof of knowledge for each exponent (each proof is two big-endian unsigned integers).
/// </summary>
/// <remarks>
/// Every field is a <c>byte[]</c>/<c>byte[][]</c>: reference equality, so compare element-wise.
/// This is the largest message pairing sends (about 3 KB after base64) and must stay well
/// under <c>ControlMessageCodec.MaxFrameSize</c>.
/// </remarks>
public sealed record PairJPakeRound1(byte[] Gx1, byte[] Gx2, byte[][] ProofX1, byte[][] ProofX2) : ControlMessage;

/// <summary>
/// J-PAKE round 2 (RFC 8236), sent by each side: the group element <c>A</c> that carries the
/// room code, and a Schnorr proof of knowledge for the exponent behind it.
/// </summary>
/// <remarks>
/// Note: both fields are <c>byte[]</c>/<c>byte[][]</c>, which have reference equality semantics.
/// Record-level <c>==</c> comparisons will use reference equality, not value equality.
/// Tests must compare bytes element-wise or use <c>SequenceEqual</c>.
/// </remarks>
public sealed record PairJPakeRound2(byte[] A, byte[][] ProofX2s) : ControlMessage;

/// <summary>
/// First message of the mutual pairing handshake after J-PAKE: the master's challenge nonce.
/// </summary>
/// <remarks>
/// Note: <c>MasterNonce</c> is a <c>byte[]</c>, which has reference equality semantics.
/// Record-level <c>==</c> comparisons will use reference equality, not value equality.
/// Tests must compare bytes element-wise or use <c>SequenceEqual</c>.
/// </remarks>
public sealed record PairChallengeRequest(byte[] MasterNonce) : ControlMessage;

/// <summary>
/// Second message: the agent's proof over both nonces, plus the agent's own nonce — which is
/// what forces the master to prove the pre-shared key in turn (see <c>PairingCrypto</c>).
/// </summary>
/// <remarks>
/// Note: both fields are <c>byte[]</c>, which has reference equality semantics.
/// Record-level <c>==</c> comparisons will use reference equality, not value equality.
/// Tests must compare bytes element-wise or use <c>SequenceEqual</c>.
/// </remarks>
public sealed record PairChallengeResponse(byte[] AgentProof, byte[] AgentNonce) : ControlMessage;

/// <summary>
/// Third message: the master's verdict, carrying the master's own proof over both nonces when
/// the verdict is success. The agent must verify <see cref="MasterProof"/> before it pins
/// anything — <see cref="Success"/> alone is a claim any machine on the LAN can make.
/// </summary>
/// <remarks>
/// <c>PairAck(Success: false, MasterProof: null)</c> — see <see cref="Rejected"/> — is also the
/// abort signal either side sends when it gives up mid-handshake, so the peer fails fast instead
/// of blocking on a receive that will never complete.
/// <para>
/// Note: <c>MasterProof</c> is a <c>byte[]</c>, which has reference equality semantics.
/// Record-level <c>==</c> comparisons will use reference equality, not value equality.
/// Tests must compare bytes element-wise or use <c>SequenceEqual</c>.
/// </para>
/// </remarks>
public sealed record PairAck(bool Success, byte[]? MasterProof = null) : ControlMessage
{
    /// <summary>The "pairing did not happen" signal. Carries no proof, by definition.</summary>
    public static PairAck Rejected { get; } = new(Success: false, MasterProof: null);
}

/// <summary>
/// The agent's explicit "the pairing is recorded", sent after it has verified the master's
/// proof and pinned the master's certificate. The master pins only when it has read this.
/// </summary>
/// <remarks>
/// Success has to be something the agent says, not something the master infers. The stream
/// closing, a wait running out, or the connection going away all happen for reasons that have
/// nothing to do with acceptance — an agent that timed out closes its stream too — so none of
/// them may be read as one. It carries no proof of its own: it travels on the TLS connection the
/// handshake has just bound to both certificates.
/// </remarks>
public sealed record PairConfirm : ControlMessage;

public sealed record StartBroadcast(int Width, int Height, int TargetFps) : ControlMessage;
public sealed record StopBroadcast : ControlMessage;
public sealed record BroadcastReady : ControlMessage;
