using System.Diagnostics;
using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Tests.Encoding;

// The encoder's input/output accounting, driven by a fake transform: no GPU, no Media Foundation.
public class EncoderPipelineTests
{
    // Short waits so a test that expects a timeout does not take seconds.
    private static readonly EncoderTimeouts Short = new(Input: TimeSpan.FromMilliseconds(400), Output: TimeSpan.FromMilliseconds(40));

    private static EncoderPipeline<int> Pipeline(FakeMft mft, EncoderTimeouts? timeouts = null, byte[]? typeParameterSets = null)
    {
        var sets = new H264ParameterSets();
        sets.Seed(typeParameterSets);
        return new EncoderPipeline<int>(mft, timeouts ?? Short, sets);
    }

    // ---- the plain cases, both kinds of transform -------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneInOneOut_WhenTheEncoderKeepsUp(bool asynchronous)
    {
        var pipeline = Pipeline(new FakeMft(asynchronous));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(pipeline.TryEncode(i, forceKeyframe: i == 0, out var encoded));
            Assert.Equal(i, FakeMft.IndexOf(encoded.Data));
            Assert.Equal((uint)i, encoded.FrameId);
            Assert.Equal(i == 0, encoded.IsKeyframe);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AForcedKeyframe_IsReportedFromTheBytes(bool asynchronous)
    {
        var mft = new FakeMft(asynchronous);
        var pipeline = Pipeline(mft);

        for (var i = 0; i < 6; i++)
        {
            var force = i is 0 or 3 or 4;
            Assert.True(pipeline.TryEncode(i, force, out var encoded));
            Assert.Equal(force, encoded.IsKeyframe);
        }
        Assert.Equal(3, mft.KeyframeRequests);
    }

    // ---- E1: a refused input must not withhold a finished picture --------------------------

    [Fact]
    public void Async_AnEncoderThatAsksForInputOnlyAfterItsPictureIsTaken_DoesNotMakeEveryCallWaitOutTheInputTimeout()
    {
        var mft = new FakeMft(asynchronous: true)
        {
            AsksForInputOnlyAfterOutputIsTaken = true,
            HeldBack = i => i == 0,
        };
        // A long input wait, so sitting it out cannot pass for a slow test machine.
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromSeconds(5), Output: Short.Output));

        // The first picture takes longer than the call waits for it.
        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _));
        mft.Release();

        var clock = Stopwatch.StartNew();
        var gotOne = pipeline.TryEncode(1, forceKeyframe: false, out var encoded);
        clock.Stop();

        Assert.True(gotOne, "the finished first picture was withheld");
        Assert.Equal(0, FakeMft.IndexOf(encoded.Data));
        Assert.True(encoded.IsKeyframe);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"the call waited {clock.ElapsedMilliseconds} ms: it sat out the input timeout instead of taking the finished picture");
    }

    [Fact]
    public void Async_WhenInputIsRefused_AFinishedPictureIsStillReturned()
    {
        var mft = new FakeMft(asynchronous: true)
        {
            NeverAsksForMoreInput = true,
            HeldBack = _ => true,
        };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(20), Output: TimeSpan.FromMilliseconds(20)));

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _)); // fed, not finished within 20 ms
        mft.Release();

        // This input is refused (the encoder never asks again), but picture 0 is finished.
        Assert.True(pipeline.TryEncode(1, forceKeyframe: false, out var encoded));
        Assert.Equal(0, FakeMft.IndexOf(encoded.Data));
    }

    [Fact]
    public void Sync_WhenInputIsRefusedWhileAPictureWaits_ThePictureIsCollectedAndTheInputRetried()
    {
        var mft = new FakeMft(asynchronous: false)
        {
            RefusesInputWhileOutputWaits = true,
            HeldBack = i => i == 0,
        };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _)); // not ready yet
        mft.Release();

        Assert.True(pipeline.TryEncode(1, forceKeyframe: false, out var first));
        Assert.Equal(0, FakeMft.IndexOf(first.Data));
        Assert.True(pipeline.TryTakePending(out var second));
        Assert.Equal(1, FakeMft.IndexOf(second.Data));
    }

    // ---- E2: a late picture must not make the encoder lag for good --------------------------

    [Fact]
    public void TryTakePending_ReturnsTheHeldPicture_AfterOneLateOutput()
    {
        var mft = new FakeMft(asynchronous: true) { HeldBack = i => i == 0 };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _));
        mft.Release();

        Assert.True(pipeline.TryTakePending(out var held));
        Assert.Equal(0, FakeMft.IndexOf(held.Data));
        Assert.Equal(0u, held.FrameId);
        Assert.False(pipeline.TryTakePending(out _));
    }

    [Fact]
    public void TryTakePending_ReturnsFalseAtOnce_WhenNothingIsHeld()
    {
        foreach (var asynchronous in new[] { false, true })
        {
            // A long output wait, so sitting it out cannot pass for a slow test machine.
            var pipeline = Pipeline(new FakeMft(asynchronous), new EncoderTimeouts(Input: Short.Input, Output: TimeSpan.FromSeconds(5)));
            Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out _));

            var clock = Stopwatch.StartNew();
            var got = pipeline.TryTakePending(out _);
            clock.Stop();

            Assert.False(got);
            Assert.True(clock.ElapsedMilliseconds < 1000, $"asking for a held picture waited {clock.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void AForcedKeyframeThatCameOutLate_IsReportedAsAKeyframeWhenItIsHandedOut()
    {
        var mft = new FakeMft(asynchronous: true) { HeldBack = i => i == 1 };
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out _));

        Assert.False(pipeline.TryEncode(1, forceKeyframe: true, out _)); // the forced keyframe is late
        mft.Release();

        Assert.True(pipeline.TryTakePending(out var held));
        Assert.Equal(1, FakeMft.IndexOf(held.Data));
        Assert.True(held.IsKeyframe);
        Assert.Equal(1u, held.FrameId);
    }

    [Fact]
    public void AfterALatePicture_TheBacklogDrainsThroughTryTakePending_AndIdsStayContiguousInOrder()
    {
        var mft = new FakeMft(asynchronous: true) { HeldBack = i => i == 0 };
        var pipeline = Pipeline(mft);
        var handedOut = new List<EncodedFrame>();

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _));
        mft.Release();
        Assert.True(pipeline.TryEncode(1, forceKeyframe: false, out var encoded));
        handedOut.Add(encoded);
        while (pipeline.TryTakePending(out var more))
            handedOut.Add(more);

        Assert.Equal([0, 1], handedOut.Select(f => FakeMft.IndexOf(f.Data)));
        Assert.Equal([0u, 1u], handedOut.Select(f => f.FrameId));
    }

    // ---- E3: keyframes carry their parameter sets ------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AKeyframeThatLacksParameterSets_GetsTheOnesSeenEarlierInTheStream(bool asynchronous)
    {
        var mft = new FakeMft(asynchronous) { ParameterSets = FakeMft.ParameterSetPolicy.WithTheFirstKeyframeOnly };
        var pipeline = Pipeline(mft);

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var first));
        Assert.True(H264Stream.Inspect(first.Data).HasParameterSets);
        Assert.Equal(0, pipeline.KeyframesCompleted);

        Assert.True(pipeline.TryEncode(1, forceKeyframe: false, out var inter));
        Assert.Equal([0, 0, 0, 1, 0x41, 0x9A, 1], inter.Data); // an inter picture is never touched

        Assert.True(pipeline.TryEncode(2, forceKeyframe: true, out var second));
        var summary = H264Stream.Inspect(second.Data);
        Assert.True(second.IsKeyframe);
        Assert.True(summary.HasIdr);
        Assert.True(summary.HasParameterSets, "the second keyframe went out without SPS/PPS");
        Assert.Equal(1, pipeline.KeyframesCompleted);
    }

    [Fact]
    public void AKeyframeThatAlreadyHasParameterSets_IsUnchanged()
    {
        var pipeline = Pipeline(new FakeMft(asynchronous: false));

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var frame));

        Assert.Equal(0, pipeline.KeyframesCompleted);
        Assert.Equal([0, 0, 0, 1, .. FakeMft.Sps, 0, 0, 0, 1, .. FakeMft.Pps, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0], frame.Data);
    }

    [Fact]
    public void ParameterSetsFromTheOutputType_ServeAFirstKeyframeThatHasNone()
    {
        byte[] header = [0, 0, 0, 1, .. FakeMft.Sps, 0, 0, 0, 1, .. FakeMft.Pps];
        var pipeline = Pipeline(new FakeMft(asynchronous: false) { ParameterSets = FakeMft.ParameterSetPolicy.Never }, typeParameterSets: header);

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var frame));

        Assert.True(H264Stream.Inspect(frame.Data).HasParameterSets);
    }

    [Fact]
    public void AKeyframeWithNoParameterSetsAnywhere_FailsLoudlyInsteadOfGoingOutUndecodable()
    {
        var pipeline = Pipeline(new FakeMft(asynchronous: false) { ParameterSets = FakeMft.ParameterSetPolicy.Never });

        var ex = Assert.Throws<InvalidOperationException>(() => pipeline.TryEncode(0, forceKeyframe: true, out _));

        Assert.Contains("parameter sets", ex.Message);
    }

    // ---- parameter sets after a format change (round 2, P1) ---------------------------------

    private static readonly byte[] Idr = [0, 0, 0, 1, 0x65, 0x88, 0x84, 0];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AfterAFormatChange_KeyframesGetTheNewParameterSets_NotTheOldOnes(bool asynchronous)
    {
        var a = FakeMft.Header(0xA1);
        var b = FakeMft.Header(0xB2);
        var mft = new FakeMft(asynchronous)
        {
            ParameterSets = FakeMft.ParameterSetPolicy.Never,
            PublishedHeader = a,
            ChangesFormatOnFirstOutput = true,
            HeaderPublishedByFormatChange = b,
        };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(400), Output: TimeSpan.FromMilliseconds(250)), typeParameterSets: a);

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var frame));

        Assert.Equal([.. b, .. Idr], frame.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEncoderThatPublishesItsHeaderOnlyAfterAFormatChange_CanStart(bool asynchronous)
    {
        var b = FakeMft.Header(0xB2);
        var mft = new FakeMft(asynchronous)
        {
            ParameterSets = FakeMft.ParameterSetPolicy.Never,
            PublishedHeader = null,
            ChangesFormatOnFirstOutput = true,
            HeaderPublishedByFormatChange = b,
        };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(400), Output: TimeSpan.FromMilliseconds(250)));

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var frame));

        Assert.Equal([.. b, .. Idr], frame.Data);
    }

    [Fact]
    public void AHeaderPublishedLate_WithoutAFormatChange_IsFoundBeforeThrowing()
    {
        var mft = new FakeMft(asynchronous: false) { ParameterSets = FakeMft.ParameterSetPolicy.Never, PublishedHeader = null };
        var pipeline = Pipeline(mft);
        mft.PublishedHeader = FakeMft.Header(0xC3); // the type has one by the time the first keyframe is completed

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var frame));

        Assert.Equal([.. FakeMft.Header(0xC3), .. Idr], frame.Data);
    }

    // ---- an asynchronous transform that refuses what it asked for (round 2) ------------------

    [Fact]
    public void Async_AnInputTheTransformAskedForButRefuses_IsAnErrorNotASilentStall()
    {
        var mft = new FakeMft(asynchronous: true) { RefusesInputItAskedFor = true };
        var pipeline = Pipeline(mft);

        var ex = Assert.Throws<InvalidOperationException>(() => pipeline.TryEncode(0, forceKeyframe: true, out _));

        Assert.Contains("asked for", ex.Message);
    }

    [Fact]
    public void InputsAccepted_CountsOnlyThePicturesTheEncoderTook()
    {
        var mft = new FakeMft(asynchronous: true) { NeverAsksForMoreInput = true };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(20), Output: TimeSpan.FromMilliseconds(20)));

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out _));
        Assert.False(pipeline.TryEncode(1, forceKeyframe: false, out _)); // dropped: the encoder does not ask again
        Assert.False(pipeline.TryEncode(2, forceKeyframe: false, out _));

        Assert.Equal(1, pipeline.InputsAccepted);
    }

    // ---- E5: a format change ---------------------------------------------------------------

    [Fact]
    public void Async_AFormatChangeOnTheFirstOutput_ProducesThePictureOnTheNextEvent_AndThrowsNothing()
    {
        var mft = new FakeMft(asynchronous: true) { ChangesFormatOnFirstOutput = true };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(400), Output: TimeSpan.FromMilliseconds(250)));

        // The fake throws (as the real transform answers E_UNEXPECTED) if output is asked for without an event.
        var gotOne = pipeline.TryEncode(0, forceKeyframe: true, out var encoded);
        if (!gotOne)
        {
            Thread.Sleep(20);
            gotOne = pipeline.TryTakePending(out encoded);
        }

        Assert.True(gotOne);
        Assert.Equal(0, FakeMft.IndexOf(encoded.Data));
        Assert.True(encoded.IsKeyframe);
        Assert.Equal(2, mft.ProcessOutputCalls); // one that met the change, one for the picture
    }

    [Fact]
    public void Sync_AFormatChange_IsRetriedAtOnce()
    {
        var mft = new FakeMft(asynchronous: false) { ChangesFormatOnFirstOutput = true };
        var pipeline = Pipeline(mft);

        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out var encoded));

        Assert.Equal(0, FakeMft.IndexOf(encoded.Data));
    }

    // ---- a keyframe request is only made for a picture the encoder is about to take ---------

    [Fact]
    public void AKeyframeIsNotRequested_WhenTheEncoderNeverTakesThePicture()
    {
        var mft = new FakeMft(asynchronous: true) { NeverAsksForMoreInput = true };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(20), Output: TimeSpan.FromMilliseconds(20)));
        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out _));

        // The encoder does not ask again, so this picture is dropped before any request is made for it:
        // nothing is left pending to turn a later picture into an unrequested keyframe.
        Assert.False(pipeline.TryEncode(1, forceKeyframe: true, out _));

        Assert.Equal(1, mft.KeyframeRequests);
    }
}