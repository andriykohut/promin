using System.Text.Json.Serialization;

namespace Educast.Protocol.Messages;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PairChallengeRequest), "pairChallengeRequest")]
[JsonDerivedType(typeof(PairChallengeResponse), "pairChallengeResponse")]
[JsonDerivedType(typeof(PairAck), "pairAck")]
[JsonDerivedType(typeof(StartBroadcast), "startBroadcast")]
[JsonDerivedType(typeof(StopBroadcast), "stopBroadcast")]
[JsonDerivedType(typeof(BroadcastReady), "broadcastReady")]
public abstract record ControlMessage;

/// <summary>
/// First message of the mutual pairing handshake: the master's challenge nonce.
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
/// </remarks>
public sealed record PairAck(bool Success, byte[]? MasterProof = null) : ControlMessage
{
    /// <summary>The "pairing did not happen" signal. Carries no proof, by definition.</summary>
    public static PairAck Rejected { get; } = new(Success: false, MasterProof: null);
}

public sealed record StartBroadcast(int Width, int Height, int TargetFps) : ControlMessage;
public sealed record StopBroadcast : ControlMessage;
public sealed record BroadcastReady : ControlMessage;
