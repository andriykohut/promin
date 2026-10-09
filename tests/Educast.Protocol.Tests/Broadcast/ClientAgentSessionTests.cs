using Educast.Protocol.Broadcast;
using Educast.Protocol.Transport;
using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Broadcast;

public class ClientAgentSessionTests
{
    private sealed record Offered(uint FrameId, bool IsKeyframe, byte[] Bytes);

    /// <summary>
    /// A presenter with no GPU. It shows whatever it is offered unless told to refuse.
    /// </summary>
    private sealed class FakePresenter : IFramePresenter
    {
        public List<Offered> Offers { get; } = [];

        /// <summary>Called for each offer, after it is recorded; returns whether the picture was shown.</summary>
        public Func<Offered, bool> Decide { get; set; } = _ => true;

        public bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
        {
            var offered = new Offered(frameId, isKeyframe, encodedFrame.ToArray());
            Offers.Add(offered);
            _lastDecoded = offered.FrameId;
            return Decide(offered);
        }

        private uint _lastDecoded;

        /// <summary>The ids of the pictures actually shown, in order.</summary>
        public List<uint> PresentedIds { get; } = [];

        public void PresentDecoded() => PresentedIds.Add(_lastDecoded);

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

    /// <summary>
    /// A decoder that gives each picture back one call late, as a decoder that holds one picture back does: the
    /// keyframe gives nothing, the next frame gives the keyframe's picture, and so on. Offered frames and shown
    /// pictures therefore differ by exactly one.
    /// </summary>
    private sealed class OneLatePresenter : IFramePresenter
    {
        private bool _holding;

        public List<uint> OfferedIds { get; } = [];

        public bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
        {
            OfferedIds.Add(frameId);
            var pictureComesOut = _holding;
            _holding = true;
            return pictureComesOut;
        }

        public void PresentDecoded()
        {
        }
    }

    /// <summary>
    /// A decoder that behaves like the real one after a keyframe it could not use: it refuses the non-keyframes
    /// that follow without decoding them, until its next usable keyframe. <paramref name="unusableKeyframes"/>
    /// are the ids of keyframes it cannot use.
    /// </summary>
    private sealed class GatedPresenter(params uint[] unusableKeyframes) : IFramePresenter
    {
        private bool _usable;

        public List<uint> OfferedIds { get; } = [];

        public bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
        {
            OfferedIds.Add(frameId);
            if (isKeyframe)
            {
                _usable = !unusableKeyframes.Contains(frameId);
            }

            return _usable;
        }

        public void PresentDecoded()
        {
        }
    }

    [Fact]
    public void ADecoderThatHoldsOnePictureBack_IsOfferedEveryFrameAfterTheKeyframe_AndNothingIsDiscarded()
    {
        var presenter = new OneLatePresenter();
        var session = new ClientAgentSession(presenter);
        var shown = new List<uint>();
        session.FramePresented += shown.Add;

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false)); // not discarded: the decoder needs it to give picture 0
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));

        Assert.Equal([0u, 1u, 2u, 3u], presenter.OfferedIds);
        Assert.Equal(0, session.DiscardedFrameCount);
        Assert.Equal(3, session.PresentedFrameCount); // trails the four offered by exactly one
        Assert.Equal(1, session.RefusedFrameCount);   // the keyframe's own call, which had nothing to give
        Assert.Equal(3, shown.Count);
    }

    [Fact]
    public void AKeyframeTheDecoderRefuses_IsFollowedByOfferedNonKeyframes_CountedAsRefused_NotDiscarded()
    {
        var presenter = new GatedPresenter(unusableKeyframes: 0);
        var session = new ClientAgentSession(presenter);
        var shown = new List<uint>();
        session.FramePresented += shown.Add;

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));  // refused outright
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false)); // the decoder refuses it without decoding
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(3, isKeyframe: true));  // its next keyframe
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false));

        Assert.Equal([0u, 1u, 2u, 3u, 4u], presenter.OfferedIds);
        Assert.Equal([3u, 4u], shown);
        Assert.Equal(3, session.RefusedFrameCount);
        Assert.Equal(0, session.DiscardedFrameCount);
    }

    [Fact]
    public void ARefusedFrame_DoesNotStopTheFramesThatFollow()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        presenter.Decide = offered => offered.FrameId != 1;
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false)); // refused

        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));

        Assert.Equal([0u, 1u, 2u], presenter.OfferedIds);
        Assert.Equal([0u, 2u], shown);
        Assert.Equal(0, session.DiscardedFrameCount);
    }

    [Fact]
    public void AfterAGap_AKeyframeThatIsOfferedEndsTheWait_WhateverTheDecoderMadeOfIt()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false)); // gap: discarded
        presenter.Decide = offered => !offered.IsKeyframe;           // refuses the keyframe, shows the rest
        session.OnVideoFrameReceived(Payload(3, isKeyframe: true));

        session.OnVideoFrameReceived(Payload(4, isKeyframe: false)); // offered: a keyframe has been offered

        Assert.Equal([0u, 3u, 4u], presenter.OfferedIds);
        Assert.Equal([0u, 4u], shown);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(1, session.DiscardedFrameCount); // only the frame after the gap
    }

    [Fact]
    public void AGap_ThenNonKeyframes_ThenAKeyframe_DiscardsUntilTheKeyframeIsOffered()
    {
        var presenter = new GatedPresenter();
        var session = new ClientAgentSession(presenter);
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));

        session.OnVideoFrameReceived(Payload(3, isKeyframe: false)); // 2 never arrived
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(5, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(6, isKeyframe: false));

        Assert.Equal([0u, 1u, 5u, 6u], presenter.OfferedIds);
        Assert.Equal(2, session.DiscardedFrameCount);
    }

    [Fact]
    public void AStartMidStream_DiscardsNonKeyframesAgain_UntilAKeyframeHasBeenOffered()
    {
        var presenter = new GatedPresenter();
        var session = new ClientAgentSession(presenter);
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false));

        session.OnBroadcastStarted();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));
        session.OnVideoFrameReceived(Payload(2, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false));

        Assert.Equal([0u, 1u, 2u, 2u, 3u], presenter.OfferedIds);
        Assert.Equal(2, session.DiscardedFrameCount);
    }


    // ---- Staying live: only the newest picture of a waiting batch is shown --------------------------

    [Fact]
    public void ABatchOfThreeWaitingFrames_IsDecodedInOrder_AndPresentsOnce_TheNewest()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false), moreWaiting: false);

        Assert.Equal([0u, 1u, 2u], presenter.OfferedIds);   // every frame was decoded, in order
        Assert.Equal([2u], presenter.PresentedIds);          // one picture was shown: the newest
        Assert.Equal([2u], shown);
        Assert.Equal(1, session.PresentedFrameCount);
        Assert.Equal(2, session.SkippedPictureCount);
        Assert.Equal(0, session.DiscardedFrameCount);
        Assert.Equal(0, session.RefusedFrameCount);
    }

    [Fact]
    public void UnderPermanentBacklog_EveryThirdDecodedPictureIsShown()
    {
        var (session, presenter, shown) = Create();

        for (uint id = 1; id <= 9; id++)
        {
            session.OnVideoFrameReceived(Payload(id, isKeyframe: id == 1), moreWaiting: true);
        }

        Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u], presenter.OfferedIds);
        Assert.Equal([3u, 6u, 9u], presenter.PresentedIds);
        Assert.Equal([3u, 6u, 9u], shown);
        Assert.Equal(3, session.PresentedFrameCount);
        Assert.Equal(6, session.SkippedPictureCount);
    }

    [Fact]
    public void AStart_RestartsTheCountOfConsecutiveSkips()
    {
        var (session, presenter, shown) = Create();
        // One picture replaced unshown; picture 1 is pending and one more skip would force a picture out.
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false), moreWaiting: true);
        Assert.Empty(shown);

        session.OnBroadcastStarted(); // gives up pending picture 1 as well as clearing the count
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false), moreWaiting: true);
        Assert.Empty(shown); // a count left over from before would have forced picture 1 out here

        session.OnVideoFrameReceived(Payload(2, isKeyframe: false), moreWaiting: true);
        Assert.Equal([2u], shown); // two skips since the start, so the third picture shows
    }

    [Fact]
    public void AKeyframeInTheMiddleOfABatch_IsStillDecoded()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(2, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(3, isKeyframe: false), moreWaiting: false);

        Assert.Equal([0u, 1u, 2u, 3u], presenter.OfferedIds);
        Assert.True(presenter.Offers[2].IsKeyframe);
        Assert.Equal([2u, 3u], presenter.PresentedIds); // two skipped in a row, so the keyframe's picture is shown
        Assert.Equal(2, session.SkippedPictureCount);
    }

    [Fact]
    public void FramesThatArriveOneAtATime_AreAllShown_AsBefore()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));

        Assert.Equal([0u, 1u], presenter.PresentedIds);
        Assert.Equal(0, session.SkippedPictureCount);
    }

    [Fact]
    public void ABatchThatEndsWithADiscardedFrame_StillShowsThePictureBeforeIt()
    {
        var (session, presenter, shown) = Create();

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(2, isKeyframe: false), moreWaiting: false); // a gap: discarded

        Assert.Equal([0u], presenter.PresentedIds);
        Assert.Equal(1, session.DiscardedFrameCount);
        Assert.Equal(0, session.SkippedPictureCount);
    }

    [Fact]
    public void ABatchThatEndsWithARefusedFrame_ShowsNothing_AndCountsTheLostPicture()
    {
        var (session, presenter, shown) = Create();
        presenter.Decide = offered => offered.FrameId != 1;

        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false), moreWaiting: false); // refused: picture 0 is gone with it

        Assert.Empty(presenter.PresentedIds);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(1, session.SkippedPictureCount);
        Assert.Equal(0, session.PresentedFrameCount);
    }

    [Fact]
    public void AStartBetweenFrames_GivesUpAPictureThatWasWaitingToBeShown()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true), moreWaiting: true);

        session.OnBroadcastStarted();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));

        Assert.Equal([0u], presenter.PresentedIds); // only the new stream's picture
        Assert.Equal(1, session.SkippedPictureCount);
    }

    /// <summary>
    /// What happens when the student PC cannot keep up and the bound on waiting frames is hit: the oldest are
    /// lost in the receive queue, so the next frame to arrive has a gap before it. The session sees only that:
    /// it keeps the picture on screen, discards until the next keyframe, and then is live again.
    /// </summary>
    [Fact]
    public void WhenAnOverloadedPcLosesFramesInItsQueue_TheGapIsHandled_LastPictureKept_ThenLiveAtTheNextKeyframe()
    {
        var (session, presenter, shown) = Create();
        session.OnVideoFrameReceived(Payload(0, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(1, isKeyframe: false));

        // Frames 2 to 5 were dropped from the receive queue; 6 and 7 are what was left.
        session.OnVideoFrameReceived(Payload(6, isKeyframe: false), moreWaiting: true);
        session.OnVideoFrameReceived(Payload(7, isKeyframe: false), moreWaiting: false);
        session.OnVideoFrameReceived(Payload(8, isKeyframe: true));
        session.OnVideoFrameReceived(Payload(9, isKeyframe: false));

        Assert.Equal([0u, 1u, 8u, 9u], presenter.PresentedIds); // nothing new on screen between 1 and 8
        Assert.Equal(2, session.DiscardedFrameCount);           // 6 and 7
        Assert.Equal(0, session.DroppedFrameCount);             // the loss itself is invisible: only the gap shows
        Assert.Equal(0, session.RefusedFrameCount);
        Assert.Equal(0, session.SkippedPictureCount);
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
        session.OnVideoFrameReceived(Payload(4, isKeyframe: false));   // presented: the keyframe before it was offered

        Assert.Equal(2, session.PresentedFrameCount);
        Assert.Equal(2, session.DroppedFrameCount);
        Assert.Equal(1, session.RefusedFrameCount);
        Assert.Equal(1, session.DiscardedFrameCount);
    }
}
