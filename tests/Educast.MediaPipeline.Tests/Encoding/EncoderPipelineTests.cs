using System.Diagnostics;
using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

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
            DelayFor = i => i == 0 ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero,
        };
        var pipeline = Pipeline(mft);

        // The first picture takes longer than the call waits for it.
        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _));

        var clock = Stopwatch.StartNew();
        var gotOne = pipeline.TryEncode(1, forceKeyframe: false, out var encoded);
        clock.Stop();

        Assert.True(gotOne, "the finished first picture was withheld");
        Assert.Equal(0, FakeMft.IndexOf(encoded.Data));
        Assert.True(encoded.IsKeyframe);
        Assert.True(clock.ElapsedMilliseconds < 300, $"the call waited {clock.ElapsedMilliseconds} ms: it sat out the input timeout instead of taking the finished picture");
    }

    [Fact]
    public void Async_WhenInputIsRefused_AFinishedPictureIsStillReturned()
    {
        var mft = new FakeMft(asynchronous: true)
        {
            NeverAsksForMoreInput = true,
            DelayFor = _ => TimeSpan.FromMilliseconds(100),
        };
        var pipeline = Pipeline(mft, new EncoderTimeouts(Input: TimeSpan.FromMilliseconds(20), Output: TimeSpan.FromMilliseconds(20)));

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _)); // fed, not finished within 20 ms
        Thread.Sleep(150);

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
            DelayFor = i => i == 0 ? TimeSpan.FromMilliseconds(80) : TimeSpan.Zero,
        };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _)); // not ready yet
        Thread.Sleep(120);

        Assert.True(pipeline.TryEncode(1, forceKeyframe: false, out var first));
        Assert.Equal(0, FakeMft.IndexOf(first.Data));
        Assert.True(pipeline.TryTakePending(out var second));
        Assert.Equal(1, FakeMft.IndexOf(second.Data));
    }

    // ---- E2: a late picture must not make the encoder lag for good --------------------------

    [Fact]
    public void TryTakePending_ReturnsTheHeldPicture_AfterOneLateOutput()
    {
        var mft = new FakeMft(asynchronous: true) { DelayFor = i => i == 0 ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero };
        var pipeline = Pipeline(mft);

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _));
        Thread.Sleep(160);

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
            var pipeline = Pipeline(new FakeMft(asynchronous));
            Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out _));

            var clock = Stopwatch.StartNew();
            var got = pipeline.TryTakePending(out _);
            clock.Stop();

            Assert.False(got);
            Assert.True(clock.ElapsedMilliseconds < 30, $"asking for a held picture waited {clock.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void AForcedKeyframeThatCameOutLate_IsReportedAsAKeyframeWhenItIsHandedOut()
    {
        var mft = new FakeMft(asynchronous: true) { DelayFor = i => i == 1 ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero };
        var pipeline = Pipeline(mft);
        Assert.True(pipeline.TryEncode(0, forceKeyframe: true, out _));

        Assert.False(pipeline.TryEncode(1, forceKeyframe: true, out _)); // the forced keyframe is late
        Thread.Sleep(160);

        Assert.True(pipeline.TryTakePending(out var held));
        Assert.Equal(1, FakeMft.IndexOf(held.Data));
        Assert.True(held.IsKeyframe);
        Assert.Equal(1u, held.FrameId);
    }

    [Fact]
    public void AfterALatePicture_TheBacklogDrainsThroughTryTakePending_AndIdsStayContiguousInOrder()
    {
        var mft = new FakeMft(asynchronous: true) { DelayFor = i => i == 0 ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero };
        var pipeline = Pipeline(mft);
        var handedOut = new List<EncodedFrame>();

        Assert.False(pipeline.TryEncode(0, forceKeyframe: true, out _));
        Thread.Sleep(160);
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