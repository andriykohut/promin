using Educast.Protocol.Broadcast;
using Educast.Protocol.Transport;
using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Broadcast;

public class ClientAgentSessionTests
{
    private sealed record Offered(uint FrameId, bool IsKeyframe, byte[] Bytes);

    /// <summary>
    /// A presenter with no GPU. It shows whatever it is offered unless told to refuse, and its
    /// <see cref="NeedsKeyframe"/> is whatever the test last set (a real decoder's moves with its own state).
    /// </summary>
    private sealed class FakePresenter : IFramePresenter
    {
        public List<Offered> Offers { get; } = [];

        public bool NeedsKeyframe { get; set; }

        /// <summary>Called for each offer, after it is recorded; returns whether the picture was shown.</summary>
        public Func<Offered, bool> Decide { get; set; } = _ => true;

        public bool TryDecodeAndPresent(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
        {
            var offered = new Offered(frameId, isKeyframe, encodedFrame.ToArray());
            Offers.Add(offered);
            return Decide(offered);
        }

        public uint[] OfferedIds => [.. Offers.Select(o => o.FrameId)];
    }

    private static byte[] Payload(uint frameId, bool isKeyframe, params byte[] encodedFrame)
    {
        var payload = new byte[VideoFrameEnvelope.Size + encodedFrame.Length];
        new VideoFrameEnvelope(frameId, isKeyframe).WriteTo(payload);
        encodedFrame.CopyTo(payload.AsSpan(VideoFrameEnvelope.Size));
        return payload;
    }

    private static byte[] Payload(uint frameId, bool isKeyframe) => Payload(frameId, isKeyframe, [(byte)frameId]);

    private static (ClientAgentSession Session, FakePresenter Presenter, List<uint> Shown) Create()
    {
        var presenter = new FakePresenter();
        var session = new ClientAgentSession(presenter);
        var shown = new List<uint>();
        session.FramePresented += shown.Add;
        return (session, presenter, shown);
    }

    // ---- The brief's two, for the new seam -------------------------------------------------------

    [Fact]
    public void OnVideoFrameReceived_PresentsAnAcceptedFrame_AndSaysSo()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(frameId: 1, isKeyframe: true, 1, 2, 3, 4, 5));

        var offered = Assert.Single(presenter.Offers);
        Assert.Equal(1u, offered.FrameId);
        Assert.True(offered.IsKeyframe);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, offered.Bytes); // the encoded frame, without its envelope
        Assert.Equal([1u], shown);
        Assert.Equal(1, session.PresentedFrameCount);
        Assert.Equal(0, session.DroppedFrameCount);
        Assert.Equal(0, session.DiscardedFrameCount);
        Assert.Equal(0, session.RefusedFrameCount);
    }

    [Fact]
    public void OnVideoFrameReceived_IgnoresAFrameOlderThanTheOneAlreadyShown()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(5, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false)); // would have jumped the viewer backwards

        Assert.Equal([5u], shown);
        Assert.Equal([5u], presenter.OfferedIds);
        Assert.Equal(1, session.DroppedFrameCount);
        Assert.Equal(0, session.DiscardedFrameCount);
    }

    [Fact]
    public void OnVideoFrameReceived_IgnoresAFrameSeenTwice()
    {
        var (session, presenter, _) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));

        Assert.Equal([0u], presenter.OfferedIds);
        Assert.Equal(1, session.DroppedFrameCount);
    }

    [Fact]
    public void FramesInOrder_AreAllShown()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));

        Assert.Equal([0u, 1u, 2u], shown);
        Assert.Equal(3, session.PresentedFrameCount);
        Assert.Equal(0, session.DiscardedFrameCount);
    }

    // ---- A gap in the ids: nothing is decoded until a keyframe ------------------------------------

    [Fact]
    public void AGap_DiscardsTheDeltasThatFollow_UntilTheNextKeyframe()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false)); // 2 never arrived
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false)); // follows 3 directly, but 3 was not shown
        session.OnVideoFrameReceived(Payload(5, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(6, isKeyframe: false));

        Assert.Equal([0u, 1u, 5u, 6u], presenter.OfferedIds); // the decoder never saw 3 or 4
        Assert.Equal([0u, 1u, 5u, 6u], shown);
        Assert.Equal(2, session.DiscardedFrameCount);
        Assert.Equal(0, session.DroppedFrameCount); // 3 and 4 were in order: the gate has no quarrel with them
    }

    [Fact]
    public void AGap_ThatIsFollowedByAKeyframe_CostsNothing()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(4, isKeyframe: true)); // 2 and 3 never arrived; a keyframe crosses any gap
        session.OnVideoFrameReceived(Payload(5, isKeyframe: false));

        Assert.Equal([0u, 1u, 4u, 5u], shown);
        Assert.Equal(0, session.DiscardedFrameCount);
    }

    [Fact]
    public void TwoGaps_AreOneWaitForOneKeyframe()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // gap
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false)); // another gap, still waiting
        session.OnVideoFrameReceived(Payload(5, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(8, isKeyframe: true));  // a third gap, crossed by the keyframe

        Assert.Equal([0u, 8u], shown);
        Assert.Equal(3, session.DiscardedFrameCount);
    }

    [Fact]
    public void ALateFrame_ThatTheGateRefuses_DoesNotRepairAGap()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // 1 is still on its way
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false)); // it arrives: older than 2, which was accepted
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));

        // Frame 1 was never going to be decoded after 2; the chain is broken until a keyframe.
        Assert.Equal([0u], shown);
        Assert.Equal(1, session.DroppedFrameCount);
        Assert.Equal(2, session.DiscardedFrameCount); // 2 and 3
    }

    [Fact]
    public void TheFirstFrameEver_MustBeAKeyframe()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(40, isKeyframe: false)); // joined mid-stream, or the keyframe was lost
        session.OnVideoFrameReceived(Payload(41, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(42, isKeyframe: true));

        Assert.Equal([42u], shown);
        Assert.Equal([42u], presenter.OfferedIds);
        Assert.Equal(2, session.DiscardedFrameCount);
    }

    [Fact]
    public void ADiscardedFrame_IsNotShown_AndTheLastPictureStays()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));

        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));

        // Nothing was offered for it, so nothing replaced the picture on screen; no event, no count.
        Assert.Equal([0u], presenter.OfferedIds);
        Assert.Equal([0u], shown);
        Assert.Equal(1, session.PresentedFrameCount);
    }

    // ---- A master that starts again ---------------------------------------------------------------

    [Fact]
    public void ABroadcastThatStartsAgain_IsShownFromItsFirstKeyframe_OnTheSameSession()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(500, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(501, isKeyframe: false));

        session.OnBroadcastStarted(); // the master restarted: its frame ids count from 0 again
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));

        Assert.Equal([500u, 501u, 0u, 1u], shown);
        Assert.Equal(0, session.DroppedFrameCount);
    }

    [Fact]
    public void WithoutAStart_ARestartedMastersFramesAreAllRefused()
    {
        // The reason OnBroadcastStarted exists: this is what a gate that was never reset does.
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(500, isKeyframe: true));

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: true));

        Assert.Equal([500u], shown);
        Assert.Equal(2, session.DroppedFrameCount);
    }

    [Fact]
    public void AStart_ForgetsAWaitForAKeyframe_AndExpectsOneFirst()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // a gap: waiting for a keyframe

        session.OnBroadcastStarted();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: false)); // the new stream's first frame is not a keyframe
        session.OnVideoFrameReceived(Payload(1, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // in order after 1: no gap left over from before

        Assert.Equal([0u, 1u, 2u], shown);
        Assert.Equal(2, session.DiscardedFrameCount); // the gap's frame, and the new stream's first delta
    }

    [Fact]
    public void AStart_KeepsTheRunningTotals()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(5, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false));  // dropped
        session.OnVideoFrameReceived(Payload(9, isKeyframe: false));  // discarded
        presenter.Decide = _ => false;
        session.OnVideoFrameReceived(Payload(10, isKeyframe: true));  // refused

        session.OnBroadcastStarted();

        Assert.Equal(1, session.PresentedFrameCount);
        Assert.Equal(1, session.DroppedFrameCount);
        Assert.Equal(1, session.DiscardedFrameCount);
        Assert.Equal(1, session.RefusedFrameCount);
    }

    // ---- A presenter that cannot show what it is given --------------------------------------------

    [Fact]
    public void ARefusedFrame_IsNotShown_AndNotCountedAsShown()
    {
        var (session, presenter, shown) = Create();
        presenter.Decide = _ => false;

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));

        Assert.Empty(shown);
        Assert.Equal(0, session.PresentedFrameCount);
        Assert.Equal(1, session.RefusedFrameCount);
    }

    [Fact]
    public void WhenARefusalLeavesThePresenterNeedingAKeyframe_DeltasAreNotFedToIt()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        presenter.Decide = offered => offered.FrameId != 1;
        presenter.NeedsKeyframe = true; // what a decoder says after it refused bytes
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false)); // refused

        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // in order, but the decoder cannot use it
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));
        presenter.NeedsKeyframe = false; // a keyframe came out
        session.OnVideoFrameReceived(Payload(4, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(5, isKeyframe: false));

        Assert.Equal([0u, 1u, 4u, 5u], presenter.OfferedIds);
        Assert.Equal([0u, 4u, 5u], shown);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(2, session.DiscardedFrameCount);
    }

    [Fact]
    public void WhenARefusedKeyframeLeavesThePresenterNeedingAKeyframe_ItWaitsForTheNextOne()
    {
        var (session, presenter, shown) = Create();
        presenter.NeedsKeyframe = true;
        presenter.Decide = offered =>
        {
            presenter.NeedsKeyframe = offered.FrameId == 0; // a picture came out of the second keyframe
            return offered.FrameId != 0;
        };
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true)); // junk keyframe: refused
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));

        Assert.Equal([0u, 2u, 3u], presenter.OfferedIds);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(1, session.DiscardedFrameCount);
    }

    [Fact]
    public void ARefusalThatDoesNotNeedAKeyframe_DoesNotStopTheDeltasThatFollow()
    {
        // A decoder can swallow an input or hold a picture back without anything being wrong with the stream.
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        presenter.Decide = offered => offered.FrameId != 1;
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false)); // refused, NeedsKeyframe stays false

        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));

        Assert.Equal([0u, 1u, 2u], presenter.OfferedIds);
        Assert.Equal([0u, 2u], shown);
        Assert.Equal(0, session.DiscardedFrameCount);
    }

    [Fact]
    public void WhenAShownPictureLeavesThePresenterNeedingAKeyframe_TheNextDeltaIsDiscarded()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));

        presenter.NeedsKeyframe = true; // the picture came out, and then the stream broke
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));
        presenter.NeedsKeyframe = false;
        session.OnVideoFrameReceived(Payload(4, isKeyframe: true));

        Assert.Equal([0u, 1u, 2u, 4u], shown); // 2 was shown (its picture came out); 3 was not fed
        Assert.Equal(1, session.DiscardedFrameCount);
    }

    [Fact]
    public void AKeyframeThatIsOfferedButGivesNoPicture_DoesNotEndAWait()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // gap: waiting for a keyframe
        presenter.Decide = _ => false;                               // a decoder that swallows the keyframe
        session.OnVideoFrameReceived(Payload(3, isKeyframe: true));
        presenter.Decide = _ => true;

        session.OnVideoFrameReceived(Payload(4, isKeyframe: false)); // nothing was shown from 3: still unusable
        session.OnVideoFrameReceived(Payload(5, isKeyframe: true));

        Assert.Equal([0u, 5u], shown);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(2, session.DiscardedFrameCount); // 2 and 4
    }

    [Fact]
    public void APresenterThatThrows_IsNotHiddenFromTheCaller()
    {
        var (session, presenter, _) = Create();
        presenter.Decide = _ => throw new InvalidOperationException("the GPU went away");

        Assert.Throws<InvalidOperationException>(() => session.OnVideoFrameReceived(Payload(0, isKeyframe: true)));
    }

    // ---- Payloads that are not frames --------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(VideoFrameEnvelope.Size - 1)]
    public void ATooShortPayload_IsDroppedAndCounted_AndThrowsNothing(int length)
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(new byte[length]);

        Assert.Empty(presenter.Offers);
        Assert.Empty(shown);
        Assert.Equal(1, session.DroppedFrameCount);
    }

    [Fact]
    public void AnEnvelopeWithNoFrameAfterIt_IsHandedOnAsAnEmptyFrame_ForThePresenterToRefuse()
    {
        var (session, presenter, shown) = Create();
        presenter.Decide = _ => false;

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true, []));

        var offered = Assert.Single(presenter.Offers);
        Assert.Empty(offered.Bytes);
        Assert.Equal(1, session.RefusedFrameCount);
    }

    [Fact]
    public void ATooLargePayload_IsDroppedAndCounted_AndThrowsNothing()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(new byte[QuicChannel.MaxVideoFrameBytes + 1]);

        Assert.Empty(presenter.Offers);
        Assert.Equal(1, session.DroppedFrameCount);
    }

    [Fact]
    public void APayloadExactlyAtTheLimit_IsAFrame()
    {
        var (session, presenter, shown) = Create();
        var payload = new byte[QuicChannel.MaxVideoFrameBytes];
        new VideoFrameEnvelope(0, true).WriteTo(payload);

        session.OnVideoFrameReceived(payload);

        Assert.Equal([0u], shown);
        Assert.Equal(0, session.DroppedFrameCount);
    }

    [Fact]
    public void AMalformedPayload_LeavesTheGateWhereItWas()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(7, isKeyframe: true));

        session.OnVideoFrameReceived(new byte[2]);
        session.OnVideoFrameReceived(Payload(8, isKeyframe: false));

        // The malformed payload carried no id, so it cannot have moved the gate: 8 follows 7 and is shown.
        Assert.Equal([7u, 8u], shown);
    }

    [Fact]
    public void AFrameLostToBeingTooLarge_BreaksTheChain_LikeAnyOtherLostFrame()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));

        session.OnVideoFrameReceived(new byte[QuicChannel.MaxVideoFrameBytes + 1]); // this was frame 2
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(4, isKeyframe: true));

        Assert.Equal([0u, 1u, 4u], shown);
        Assert.Equal(1, session.DroppedFrameCount);
        Assert.Equal(1, session.DiscardedFrameCount);
    }

    // ---- Counters -----------------------------------------------------------------------------------

    [Fact]
    public void EachCounterCountsItsOwnKind_AndNothingIsCountedTwice()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));    // presented
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));    // dropped: duplicate
        session.OnVideoFrameReceived(new byte[1]);                      // dropped: malformed
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));   // discarded: gap
        presenter.Decide = _ => false;
        session.OnVideoFrameReceived(Payload(3, isKeyframe: true));    // refused
        presenter.Decide = _ => true;
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false));   // discarded: still waiting, the keyframe before it was refused

        Assert.Equal(1, session.PresentedFrameCount);
        Assert.Equal(2, session.DroppedFrameCount);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(2, session.DiscardedFrameCount);
    }
}
