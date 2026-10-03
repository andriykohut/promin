using System.Collections.Concurrent;
using Educast.Protocol.Broadcast;

namespace Educast.Protocol.Tests.TestDoubles;

public sealed class RecordingVideoSink : IVideoSink
{
    // One entry per whole frame sent: each would be one unidirectional QUIC
    // stream against a real QuicChannel.
    public ConcurrentQueue<byte[]> ReceivedFrames { get; } = new();

    public ValueTask SendFrameAsync(byte[] envelopeAndFrame, CancellationToken ct)
    {
        ReceivedFrames.Enqueue(envelopeAndFrame);
        return ValueTask.CompletedTask;
    }
}
