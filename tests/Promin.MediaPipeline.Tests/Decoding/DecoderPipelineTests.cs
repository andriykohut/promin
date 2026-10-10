using Promin.MediaPipeline.Decoding;

namespace Promin.MediaPipeline.Tests.Decoding;

// The decoder's input/output accounting, driven by a fake transform: no GPU, no Media Foundation.
public class DecoderPipelineTests
{
    private static DecoderPipeline<FakePicture> Pipeline(FakeDecoderMft mft) => new(mft);

    // ---- one in, one out ---------------------------------------------------------------------

    [Fact]
    public void OneInOneOut_EveryCallReturnsThePictureItFed()
    {
        var pipeline = Pipeline(new FakeDecoderMft());

        for (var i = 0; i < 30; i++)
        {
            var data = i % 10 == 0 ? Frames.Keyframe(i) : Frames.Delta(i);
            Assert.True(pipeline.TryDecode(data, out var picture), $"call {i} returned no picture");
            Assert.Equal(i, picture!.Index);
            Assert.Equal((1280, 720), (picture.Width, picture.Height));
        }
        Assert.Equal(0, pipeline.PicturesDropped);
        Assert.Null(pipeline.LastFailure);
    }

    // The point of "low delay": a decoder that keeps a picture back would show the presenter's still screen
    // two seconds late. The pipeline does not hide it: the call returns false and the picture comes a call late.
    [Fact]
    public void ADecoderThatHoldsAPictureBack_ShowsAsNoPictureFromTheCall_AndThePictureComesALaterCall()
    {
        var pipeline = Pipeline(new FakeDecoderMft { HoldBack = 1 });

        Assert.False(pipeline.TryDecode(Frames.Keyframe(0), out var none));
        Assert.Null(none);
        Assert.Null(pipeline.LastFailure); // holding back is not a failure

        Assert.True(pipeline.TryDecode(Frames.Delta(1), out var picture));
        Assert.Equal(0, picture!.Index); // one behind
    }

    [Fact]
    public void ANeedMoreInputAnswer_ReturnsFalse_AndKeepsTheStreamGoing()
    {
        var mft = new FakeDecoderMft { HoldBack = 2 };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(Frames.Keyframe(0), out _));
        Assert.False(pipeline.TryDecode(Frames.Delta(1), out _));
        Assert.True(pipeline.TryDecode(Frames.Delta(2), out var picture));
        Assert.Equal(0, picture!.Index);
        Assert.Equal(0, mft.FlushCount);
    }

    // ---- starting at a keyframe --------------------------------------------------------------

    [Fact]
    public void ANonKeyframeFirst_ReturnsFalse_ThrowsNothing_AndNeverReachesTheDecoder()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(Frames.Delta(0), out var picture));

        Assert.Null(picture);
        Assert.Empty(mft.Inputs);
        Assert.NotNull(pipeline.LastFailure);
        Assert.Contains("keyframe", pipeline.LastFailure);
    }

    [Fact]
    public void AKeyframeWithoutParameterSets_CannotStartTheStream()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(Frames.KeyframeWithoutParameterSets(0), out _));
        Assert.Empty(mft.Inputs);
        Assert.Contains("parameter sets", pipeline.LastFailure);

        // The next keyframe that does carry them starts it.
        Assert.True(pipeline.TryDecode(Frames.Keyframe(1), out var picture));
        Assert.Equal(1, picture!.Index);
    }

    [Fact]
    public void AfterTheStreamHasStarted_AKeyframeWithoutParameterSetsIsFine()
    {
        var pipeline = Pipeline(new FakeDecoderMft());

        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out _));
        Assert.True(pipeline.TryDecode(Frames.KeyframeWithoutParameterSets(1), out var picture));
        Assert.Equal(1, picture!.Index);
    }

    [Fact]
    public void WhatTheBytesSay_NotAFlag_DecidesWhatIsAKeyframe()
    {
        // The pipeline is given bytes only. An IDR slice starts the stream; a non-IDR slice does not, however
        // the sender labelled it.
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(Frames.Delta(0), out _));
        Assert.True(pipeline.TryDecode(Frames.Keyframe(1), out _));
        Assert.True(mft.Inputs.Single().IsKeyframe);
    }

    [Fact]
    public void TheFirstInput_IsMarkedAsADiscontinuity_AndOnlyTheFirst()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        pipeline.TryDecode(Frames.Keyframe(0), out _);
        pipeline.TryDecode(Frames.Delta(1), out _);

        Assert.Equal([true, false], mft.Inputs.Select(i => i.Discontinuity));
    }

    // ---- bytes that are not H.264 ----------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0, 0, 1, 0x65, 1, 2 })] // junk in front of the first start code
    public void BytesThatAreNotAnnexB_ReturnFalse_ThrowNothing_AndNeverReachTheDecoder(byte[] garbage)
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(garbage, out var picture));

        Assert.Null(picture);
        Assert.Empty(mft.Inputs);
        Assert.NotNull(pipeline.LastFailure);
    }

    [Fact]
    public void ANullPayload_ReturnsFalse_ThrowsNothing()
    {
        var pipeline = Pipeline(new FakeDecoderMft());

        Assert.False(pipeline.TryDecode(null, out var picture));

        Assert.Null(picture);
    }

    [Fact]
    public void GarbageInTheMiddleOfAStream_BreaksIt_UntilTheNextKeyframe()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out _));
        Assert.True(pipeline.TryDecode(Frames.Delta(1), out _));

        Assert.False(pipeline.TryDecode([9, 9, 9, 9, 9], out _));

        // A picture was lost, so the next delta would decode to a smeared picture: it is refused ...
        Assert.False(pipeline.TryDecode(Frames.Delta(2), out _));
        Assert.Equal(0, mft.RefusedForWantOfAKeyframe);
        // ... and the next keyframe starts the stream again.
        Assert.True(pipeline.TryDecode(Frames.Keyframe(3), out var picture));
        Assert.Equal(3, picture!.Index);
        Assert.True(pipeline.TryDecode(Frames.Delta(4), out picture));
        Assert.Equal(4, picture!.Index);
        Assert.Equal(1, mft.FlushCount); // the decoder was told to drop what it held
        Assert.True(mft.Inputs[^2].Discontinuity); // the keyframe after the break is marked as a discontinuity
        Assert.False(mft.Inputs[^1].Discontinuity);
    }

    [Fact]
    public void GarbageBeforeAnythingWasFed_NeedsNoFlush()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        pipeline.TryDecode([1, 2, 3], out _);
        pipeline.TryDecode([4, 5, 6], out _);

        Assert.Equal(0, mft.FlushCount);
    }

    // ---- the decoder refusing or failing ---------------------------------------------------------

    [Fact]
    public void AnInputTheDecoderRejects_ReturnsFalse_FlushesIt_AndTheNextKeyframeWorks()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out _));

        mft.RejectNextInput();
        Assert.False(pipeline.TryDecode(Frames.Delta(1), out var picture));
        Assert.Null(picture);
        Assert.Equal(1, mft.FlushCount);
        Assert.Contains("0x80004005", pipeline.LastFailure);

        Assert.False(pipeline.TryDecode(Frames.Delta(2), out _)); // refused before reaching the decoder
        Assert.Equal(0, mft.RefusedForWantOfAKeyframe);

        Assert.True(pipeline.TryDecode(Frames.Keyframe(3), out picture));
        Assert.Equal(3, picture!.Index);
    }

    [Fact]
    public void AnOutputFailure_ReturnsFalse_FlushesTheDecoder_AndTheNextKeyframeWorks()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out _));

        mft.FailNextOutput();
        Assert.False(pipeline.TryDecode(Frames.Delta(1), out _));
        Assert.Equal(1, mft.FlushCount);
        Assert.Contains("0xC00D6D72", pipeline.LastFailure);

        Assert.False(pipeline.TryDecode(Frames.Delta(2), out _));
        Assert.True(pipeline.TryDecode(Frames.Keyframe(3), out var picture));
        Assert.Equal(3, picture!.Index);
    }

    [Fact]
    public void ALastFailureFromAnEarlierCall_IsClearedBySuccess()
    {
        var pipeline = Pipeline(new FakeDecoderMft());
        pipeline.TryDecode(Frames.Delta(0), out _);
        Assert.NotNull(pipeline.LastFailure);

        pipeline.TryDecode(Frames.Keyframe(1), out _);

        Assert.Null(pipeline.LastFailure);
    }

    // ---- refused input and several pictures --------------------------------------------------------

    [Fact]
    public void WhenTheDecoderWillNotTakeInputUntilItsOutputIsCollected_TheOutputIsCollectedAndTheInputRetried()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out _));
        mft.QueueStaleOutput(77);

        Assert.True(pipeline.TryDecode(Frames.Delta(1), out var picture));

        // The newest picture is the one handed out; the older one is released.
        Assert.Equal(1, picture!.Index);
        Assert.Equal(1, pipeline.PicturesDropped);
        Assert.Equal(1, mft.Inputs.Count(i => !i.IsKeyframe)); // the delta went in once, after the retry
        Assert.Equal(0, mft.FlushCount);
    }

    [Fact]
    public void SeveralPicturesFromOneInput_HandOutTheNewest_AndReleaseTheOthers()
    {
        var mft = new FakeDecoderMft { PicturesPerInput = 3 };
        var pipeline = Pipeline(mft);

        Assert.True(pipeline.TryDecode(Frames.Keyframe(5), out var picture));

        Assert.Equal(5, picture!.Index);
        Assert.False(picture.Disposed); // the caller's now
        Assert.Equal(2, pipeline.PicturesDropped);
    }

    [Fact]
    public void ThePicturesTheCallerIsHanded_AreTheCallersToDispose_NotThePipelines()
    {
        var pipeline = Pipeline(new FakeDecoderMft());
        pipeline.TryDecode(Frames.Keyframe(0), out var first);
        pipeline.TryDecode(Frames.Delta(1), out var second);

        Assert.False(first!.Disposed);
        Assert.False(second!.Disposed);
    }

    // ---- format changes ------------------------------------------------------------------------

    [Fact]
    public void AFormatChange_IsFollowed_InTheSameCall_AndThePictureCarriesTheNewSize()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out var before));
        Assert.Equal((1280, 720), (before!.Width, before.Height));

        mft.ChangeFormatBeforeNextPicture(1366, 768);
        Assert.True(pipeline.TryDecode(Frames.Keyframe(1), out var after));

        Assert.Equal(1, after!.Index);
        Assert.Equal((1366, 768), (after.Width, after.Height));
        Assert.Equal(0, mft.FlushCount);

        // and the stream carries on at the new size
        Assert.True(pipeline.TryDecode(Frames.Delta(2), out var next));
        Assert.Equal((1366, 768), (next!.Width, next.Height));
    }

    [Fact]
    public void ADecoderThatNeverStopsChangingFormat_IsAFailure_NotAnEndlessLoop()
    {
        var mft = new FakeDecoderMft { AlwaysChangesFormat = true };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(Frames.Keyframe(0), out _));

        Assert.Contains("format", pipeline.LastFailure);
        Assert.Equal(1, mft.FlushCount);
    }

    // ---- D1: pictures held back are counted ------------------------------------------------------

    [Fact]
    public void PicturesHeldBack_CountsAnAcceptedInputThatProducedNoPicture()
    {
        var pipeline = Pipeline(new FakeDecoderMft { HoldBack = 1 });

        pipeline.TryDecode(Frames.Keyframe(0), out _); // accepted, nothing out
        Assert.Equal((1, 1), (pipeline.InputsAccepted, pipeline.PicturesHeldBack));

        pipeline.TryDecode(Frames.Delta(1), out _);    // a picture (the older one) came out
        pipeline.TryDecode(Frames.Delta(2), out _);
        Assert.Equal((3, 1), (pipeline.InputsAccepted, pipeline.PicturesHeldBack));
    }

    [Fact]
    public void PicturesHeldBack_StaysZeroForAOneInOneOutDecoder()
    {
        var pipeline = Pipeline(new FakeDecoderMft());
        for (var i = 0; i < 10; i++)
            Assert.True(pipeline.TryDecode(i == 0 ? Frames.Keyframe(i) : Frames.Delta(i), out _));

        Assert.Equal((10, 0), (pipeline.InputsAccepted, pipeline.PicturesHeldBack));
    }

    [Fact]
    public void PicturesHeldBack_DoesNotCountRefusedInput_OrInputNeverOffered_OrFailures()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);

        pipeline.TryDecode(Frames.Delta(0), out _);        // never offered: waiting for a keyframe
        pipeline.TryDecode([1, 2, 3], out _);              // not Annex B
        pipeline.TryDecode(null, out _);                   // nothing
        Assert.Equal((0, 0), (pipeline.InputsAccepted, pipeline.PicturesHeldBack));

        pipeline.TryDecode(Frames.Keyframe(1), out _);
        mft.RejectNextInput();
        pipeline.TryDecode(Frames.Delta(2), out _);        // refused by the transform
        pipeline.TryDecode(Frames.Keyframe(3), out _);
        mft.FailNextOutput();
        pipeline.TryDecode(Frames.Delta(4), out _);        // accepted, then the transform failed: a failure, not a held-back picture

        Assert.Equal(0, pipeline.PicturesHeldBack);
        Assert.Equal(3, pipeline.InputsAccepted);
    }

    // ---- D3: NeedsKeyframe -------------------------------------------------------------------------

    [Fact]
    public void NeedsKeyframe_IsTrueFromConstruction_AndStaysTrueWhileNothingStartsTheStream()
    {
        var pipeline = Pipeline(new FakeDecoderMft());
        Assert.True(pipeline.NeedsKeyframe);

        pipeline.TryDecode(Frames.Delta(0), out _);
        Assert.True(pipeline.NeedsKeyframe);
        pipeline.TryDecode(Frames.KeyframeWithoutParameterSets(1), out _);
        Assert.True(pipeline.NeedsKeyframe);
        pipeline.TryDecode([1, 2, 3], out _);
        Assert.True(pipeline.NeedsKeyframe);
    }

    [Fact]
    public void NeedsKeyframe_IsFalseOnceAKeyframeHasBeenDecoded_AndStaysFalseThroughDeltas()
    {
        var pipeline = Pipeline(new FakeDecoderMft());

        pipeline.TryDecode(Frames.Keyframe(0), out _);
        Assert.False(pipeline.NeedsKeyframe);
        pipeline.TryDecode(Frames.Delta(1), out _);
        pipeline.TryDecode(Frames.Delta(2), out _);
        Assert.False(pipeline.NeedsKeyframe);
    }

    [Fact]
    public void NeedsKeyframe_IsTrueAgainAfterGarbage_AndClearedByTheNextKeyframe()
    {
        var pipeline = Pipeline(new FakeDecoderMft());
        pipeline.TryDecode(Frames.Keyframe(0), out _);

        pipeline.TryDecode([9, 9, 9], out _);
        Assert.True(pipeline.NeedsKeyframe);
        pipeline.TryDecode(Frames.Delta(1), out _);
        Assert.True(pipeline.NeedsKeyframe);

        pipeline.TryDecode(Frames.Keyframe(2), out _);
        Assert.False(pipeline.NeedsKeyframe);
    }

    [Fact]
    public void NeedsKeyframe_IsTrueAgainAfterARefusedInput()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);

        mft.RejectNextInput();
        pipeline.TryDecode(Frames.Delta(1), out _);

        Assert.True(pipeline.NeedsKeyframe);
        Assert.Equal(1, mft.FlushCount);
    }

    [Fact]
    public void NeedsKeyframe_IsTrueAgainAfterAFailedOutput_AndAfterEndlessFormatChanges()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);
        mft.FailNextOutput();
        pipeline.TryDecode(Frames.Delta(1), out _);
        Assert.True(pipeline.NeedsKeyframe);

        var endless = Pipeline(new FakeDecoderMft { AlwaysChangesFormat = true });
        endless.TryDecode(Frames.Keyframe(0), out _);
        Assert.True(endless.NeedsKeyframe);
    }

    [Fact]
    public void NeedsKeyframe_IsNotSetByAHeldBackPicture_OrByAFormatChange()
    {
        var held = Pipeline(new FakeDecoderMft { HoldBack = 1 });
        held.TryDecode(Frames.Keyframe(0), out _);
        held.TryDecode(Frames.Delta(1), out _);
        Assert.Equal(1, held.PicturesHeldBack); // a held-back picture happened ...
        Assert.False(held.NeedsKeyframe);       // ... and re-armed nothing

        var mft = new FakeDecoderMft();
        var changing = Pipeline(mft);
        changing.TryDecode(Frames.Keyframe(0), out _);
        mft.ChangeFormatBeforeNextPicture(1366, 768);
        changing.TryDecode(Frames.Keyframe(1), out _);
        Assert.False(changing.NeedsKeyframe);
    }

    // ---- E1: NeedsKeyframe stays true until a picture has come out ---------------------------------------

    [Fact]
    public void AKeyframeAcceptedWithNoOutput_LeavesNeedsKeyframeTrue()
    {
        var mft = new FakeDecoderMft { SwallowInputs = true };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryDecode(Frames.Keyframe(0), out _));

        Assert.True(pipeline.NeedsKeyframe);
        Assert.Equal(1, pipeline.InputsAccepted);
    }

    [Fact]
    public void AfterThat_ANonKeyframeIsStillOffered_ToTheTransform()
    {
        var mft = new FakeDecoderMft { SwallowInputs = true };
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);

        pipeline.TryDecode(Frames.Delta(1), out _);

        Assert.Equal([true, false], mft.Inputs.Select(i => i.IsKeyframe)); // the delta reached the transform
    }

    [Fact]
    public void AfterThat_ANonKeyframeThatYieldsAPicture_ClearsNeedsKeyframe_AsForADecoderThatHoldsOneBack()
    {
        var pipeline = Pipeline(new FakeDecoderMft { HoldBack = 1 });
        pipeline.TryDecode(Frames.Keyframe(0), out _);
        Assert.True(pipeline.NeedsKeyframe); // one call late, which is honest

        Assert.True(pipeline.TryDecode(Frames.Delta(1), out var picture));

        Assert.Equal(0, picture!.Index);
        Assert.False(pipeline.NeedsKeyframe);
    }

    [Fact]
    public void AfterThat_ANonKeyframeThatAlsoYieldsNothing_LeavesItTrue_AndALaterKeyframeThatYieldsAPictureClearsIt()
    {
        var mft = new FakeDecoderMft { SwallowInputs = true };
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);

        Assert.False(pipeline.TryDecode(Frames.Delta(1), out _));
        Assert.True(pipeline.NeedsKeyframe);

        mft.SwallowInputs = false;
        Assert.True(pipeline.TryDecode(Frames.Keyframe(2), out _));
        Assert.False(pipeline.NeedsKeyframe);
    }

    [Fact]
    public void MidStream_AnAcceptedKeyframeWithNoOutput_DoesNotSetNeedsKeyframe()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);
        Assert.False(pipeline.NeedsKeyframe);

        mft.SwallowInputs = true;
        Assert.False(pipeline.TryDecode(Frames.Keyframe(1), out _));

        Assert.False(pipeline.NeedsKeyframe); // a swallowed picture cannot be told from a held-back one here
    }

    // After each way the stream can break, rule 1 applies again: an accepted keyframe with no output does not
    // clear the flag.
    [Theory]
    [InlineData("garbage")]
    [InlineData("refused")]
    [InlineData("failed")]
    [InlineData("formats")]
    public void AfterEachBreak_ANewKeyframeAcceptedWithNoOutput_LeavesNeedsKeyframeTrue(string how)
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);
        Assert.False(pipeline.NeedsKeyframe);

        switch (how)
        {
            case "garbage": pipeline.TryDecode([9, 9, 9], out _); break;
            case "refused": mft.RejectNextInput(); pipeline.TryDecode(Frames.Delta(1), out _); break;
            case "failed": mft.FailNextOutput(); pipeline.TryDecode(Frames.Delta(1), out _); break;
            default: mft.AlwaysChangesFormat = true; pipeline.TryDecode(Frames.Delta(1), out _); mft.AlwaysChangesFormat = false; break;
        }
        Assert.True(pipeline.NeedsKeyframe);

        mft.SwallowInputs = true;
        Assert.False(pipeline.TryDecode(Frames.Keyframe(2), out _));
        Assert.True(pipeline.NeedsKeyframe);

        mft.SwallowInputs = false;
        Assert.True(pipeline.TryDecode(Frames.Keyframe(3), out _));
        Assert.False(pipeline.NeedsKeyframe);
    }

    // ---- LastFailure and a returned picture; leaks ---------------------------------------------------

    // The doc says so: a call can hand out a picture and still report that the stream broke right after it.
    [Fact]
    public void ACallCanReturnAPicture_AndReportTheFailureThatBrokeTheStreamAfterIt()
    {
        var mft = new FakeDecoderMft { PicturesPerInput = 2, FailOutputAfterPictures = 1 };
        var pipeline = Pipeline(mft);

        Assert.True(pipeline.TryDecode(Frames.Keyframe(0), out var picture));

        Assert.NotNull(picture);
        Assert.Contains("0xC00D6D72", pipeline.LastFailure);
        Assert.True(pipeline.NeedsKeyframe);
    }

    [Fact]
    public void AnExceptionFromTheTransform_ReleasesWhatWasCollected_AndPropagates()
    {
        var mft = new FakeDecoderMft { PicturesPerInput = 2, ThrowOnOutputAfterPictures = 1 };
        var pipeline = Pipeline(mft);

        Assert.Throws<InvalidOperationException>(() => pipeline.TryDecode(Frames.Keyframe(0), out _));

        Assert.Single(mft.Created);
        Assert.True(mft.Created[0].Disposed);
    }

    [Fact]
    public void PicturesDroppedBecauseANewerOneCameOut_AreDisposed()
    {
        var mft = new FakeDecoderMft { PicturesPerInput = 3 };
        var pipeline = Pipeline(mft);

        pipeline.TryDecode(Frames.Keyframe(5), out var picture);

        Assert.Equal(3, mft.Created.Count);
        Assert.Equal(2, mft.Created.Count(p => p.Disposed));
        Assert.False(picture!.Disposed);
    }

    // ---- dispose ----------------------------------------------------------------------------------

    [Fact]
    public void Dispose_DisposesTheTransform_CanBeCalledTwice_AndTryDecodeAfterItThrows()
    {
        var mft = new FakeDecoderMft();
        var pipeline = Pipeline(mft);
        pipeline.TryDecode(Frames.Keyframe(0), out _);

        pipeline.Dispose();
        pipeline.Dispose();

        Assert.True(mft.Disposed);
        Assert.Throws<ObjectDisposedException>(() => pipeline.TryDecode(Frames.Delta(1), out _));
    }
}
