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

public sealed record PairChallengeRequest(byte[] Nonce) : ControlMessage;
public sealed record PairChallengeResponse(byte[] Hmac) : ControlMessage;
public sealed record PairAck(bool Success) : ControlMessage;
public sealed record StartBroadcast(int Width, int Height, int TargetFps) : ControlMessage;
public sealed record StopBroadcast : ControlMessage;
public sealed record BroadcastReady : ControlMessage;
