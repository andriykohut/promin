using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Video;

public class StaleFrameGateTests
{
    [Fact]
    public void TryAccept_AcceptsFramesArrivingInOrder()
    {
        var gate = new StaleFrameGate();

        Assert.True(gate.TryAccept(1));
        Assert.True(gate.TryAccept(2));
        Assert.True(gate.TryAccept(3));
        Assert.Equal(0, gate.DroppedFrameCount);
    }

    [Fact]
    public void TryAccept_AcceptsAForwardJump_WhenTheFramesInBetweenNeverArrive()
    {
        var gate = new StaleFrameGate();
        gate.TryAccept(1);

        // Frames 2-8 were aborted by the master or lost along with their
        // streams; 9 is still newer than anything rendered, so it must show.
        Assert.True(gate.TryAccept(9));
        Assert.Equal(0, gate.DroppedFrameCount);
    }

    [Fact]
    public void TryAccept_RejectsFrameOlderThanTheNewestAccepted()
    {
        var gate = new StaleFrameGate();
        gate.TryAccept(5);

        // Frame 4's stream completed after frame 5's. Rendering it now would
        // show the viewer a jump backwards in time.
        Assert.False(gate.TryAccept(4));
    }

    [Fact]
    public void TryAccept_RejectsFrameIdEqualToTheNewestAccepted()
    {
        // Exercises "equal to", distinct from "older than": an
        // implementation that only checks frameId < newest (instead of
        // <=) would wrongly accept this duplicate as a "new" frame,
        // double-rendering the same frame content.
        var gate = new StaleFrameGate();
        gate.TryAccept(5);

        Assert.False(gate.TryAccept(5));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void TryAccept_AcceptsTheVeryFirstFrame_RegardlessOfItsId(uint frameId)
    {
        // A fresh gate has no "newest" yet, so an implementation that seeds
        // the newest-seen value at 0 (instead of "none yet") would wrongly
        // reject a first frame whose id is 0.
        var gate = new StaleFrameGate();

        Assert.True(gate.TryAccept(frameId));
        Assert.Equal(0, gate.DroppedFrameCount);
    }

    [Fact]
    public void DroppedFrameCount_CountsEveryRejectedFrameExactlyOnce()
    {
        var gate = new StaleFrameGate();

        gate.TryAccept(10); // accepted
        gate.TryAccept(9);  // stale
        gate.TryAccept(10); // duplicate
        gate.TryAccept(8);  // stale
        gate.TryAccept(11); // accepted

        Assert.Equal(3, gate.DroppedFrameCount);
    }

    [Fact]
    public void DroppedFrameCount_DoesNotIncrementOnAcceptedFrames()
    {
        // Catches an implementation that increments the counter on every
        // call instead of only on rejections.
        var gate = new StaleFrameGate();

        gate.TryAccept(1);
        gate.TryAccept(2);
        gate.TryAccept(3);

        Assert.Equal(0, gate.DroppedFrameCount);
    }

    [Fact]
    public void TryAccept_SplitsTheEnvelopeFromTheEncodedFrameBytes()
    {
        var gate = new StaleFrameGate();
        byte[] encodedFrame = [1, 2, 3, 4];
        var payload = new byte[VideoFrameEnvelope.Size + encodedFrame.Length];
        new VideoFrameEnvelope(FrameId: 3, IsKeyframe: true).WriteTo(payload);
        encodedFrame.CopyTo(payload.AsSpan(VideoFrameEnvelope.Size));

        var accepted = gate.TryAccept(payload, out var envelope, out var frameBytes);

        Assert.True(accepted);
        Assert.Equal(3u, envelope.FrameId);
        Assert.True(envelope.IsKeyframe);
        Assert.Equal(encodedFrame, frameBytes.ToArray());
    }

    [Fact]
    public void TryAccept_ByteOverload_RejectsAStaleFrameAndCountsIt()
    {
        // Confirms the byte-payload overload actually routes through the
        // same staleness check as the uint overload, rather than always
        // accepting whatever it can parse.
        var gate = new StaleFrameGate();
        var newer = new byte[VideoFrameEnvelope.Size];
        new VideoFrameEnvelope(FrameId: 5, IsKeyframe: false).WriteTo(newer);
        gate.TryAccept(newer, out _, out _);

        var older = new byte[VideoFrameEnvelope.Size];
        new VideoFrameEnvelope(FrameId: 4, IsKeyframe: false).WriteTo(older);

        var accepted = gate.TryAccept(older, out var envelope, out var frameBytes);

        Assert.False(accepted);
        Assert.Equal(1, gate.DroppedFrameCount);
        Assert.Equal(default(VideoFrameEnvelope), envelope);
        Assert.True(frameBytes.IsEmpty);
    }

    [Fact]
    public void TryAccept_RejectsAndCountsAStreamTooShortToHoldAnEnvelope()
    {
        var gate = new StaleFrameGate();

        var accepted = gate.TryAccept(new byte[VideoFrameEnvelope.Size - 1], out _, out _);

        Assert.False(accepted);
        Assert.Equal(1, gate.DroppedFrameCount);
    }

    [Fact]
    public void TryAccept_AcceptsAnEmptyEncodedFrame_WhenPayloadIsExactlyEnvelopeSize()
    {
        // A stream that carries only the envelope (zero-length encoded
        // frame) is malformed content the codec/decoder should reject
        // downstream, but the gate itself must not confuse "no payload
        // bytes follow" with "too short to hold an envelope".
        var gate = new StaleFrameGate();
        var payload = new byte[VideoFrameEnvelope.Size];
        new VideoFrameEnvelope(FrameId: 1, IsKeyframe: true).WriteTo(payload);

        var accepted = gate.TryAccept(payload, out var envelope, out var frameBytes);

        Assert.True(accepted);
        Assert.Equal(1u, envelope.FrameId);
        Assert.True(frameBytes.IsEmpty);
    }

    [Fact]
    public void Reset_AllowsAFrameIdLowerThanThePreviouslyAcceptedNewest_ToBeAccepted()
    {
        // Models a master restart: the new BroadcastSession's frame counter
        // starts again at 0, which is far lower than whatever this gate last
        // saw from the prior broadcast. Without Reset, this would stay
        // rejected forever and freeze the client on its last rendered frame.
        var gate = new StaleFrameGate();
        gate.TryAccept(500);

        gate.Reset();

        Assert.True(gate.TryAccept(0));
    }

    [Fact]
    public void Reset_ResumesGating_RatherThanLeavingTheGatePermanentlyOpen()
    {
        // Catches an implementation where Reset() (or a missing Reset) ends
        // up disabling staleness checks altogether instead of merely
        // restarting the sequence: after Reset and one accepted frame, an
        // older-or-equal id must still be rejected.
        var gate = new StaleFrameGate();
        gate.TryAccept(500);
        gate.Reset();

        Assert.True(gate.TryAccept(0));
        Assert.False(gate.TryAccept(0)); // equal to new newest -> still rejected
        Assert.True(gate.TryAccept(1));  // strictly newer -> still accepted
    }

    [Fact]
    public void Reset_DoesNotClearDroppedFrameCount()
    {
        // Pins the deliberate decision that DroppedFrameCount is cumulative
        // for the lifetime of the gate, not per-broadcast: an operator
        // watching a client that keeps needing resets (repeated master
        // restarts, repeated reconnects) needs the running total to stay
        // visible rather than silently zeroing on every reset.
        var gate = new StaleFrameGate();
        gate.TryAccept(5);
        gate.TryAccept(4); // stale, counted

        gate.Reset();

        Assert.Equal(1, gate.DroppedFrameCount);

        gate.TryAccept(0);
        gate.TryAccept(0); // duplicate after reset, also counted

        Assert.Equal(2, gate.DroppedFrameCount);
    }
}
