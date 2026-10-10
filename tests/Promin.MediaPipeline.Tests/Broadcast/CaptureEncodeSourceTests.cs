using Promin.MediaPipeline.Broadcast;
using Promin.Protocol.Video;

namespace Promin.MediaPipeline.Tests.Broadcast;

// The capture-to-encode adapter's decisions, driven by a fake capture and a fake encoder: no GPU.
public class CaptureEncodeSourceTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(66);

    private readonly List<(PixelSize Size, int BitsPerSecond, FakeVideoEncoder Encoder)> _built = [];
    private FakeVideoEncoder _nextEncoder = new();

    private CaptureEncodeSource Source(FakeScreenCapture capture, QualityPreset? preset = null, Sharpness sharpness = Sharpness.Standard) =>
        new(capture, preset ?? QualityPreset.Hd15, sharpness, (size, bitsPerSecond) =>
        {
            var encoder = _nextEncoder;
            _nextEncoder = new FakeVideoEncoder();
            _built.Add((size, bitsPerSecond, encoder));
            return encoder;
        });

    private FakeVideoEncoder Encoder => _built[^1].Encoder;

    [Fact]
    public void TheEncoder_IsBuiltForTheEncodeSizeAndBitrate_OfTheFirstPicture()
    {
        var capture = new FakeScreenCapture().Frame(2560, 1440);

        var source = Source(capture, QualityPreset.Hd15, Sharpness.Sharp);

        var built = Assert.Single(_built);
        Assert.Equal(new PixelSize(1920, 1080), built.Size);
        Assert.Equal(VideoBitrate.BitsPerSecond(new PixelSize(1920, 1080), 15, Sharpness.Sharp), built.BitsPerSecond);
        Assert.Equal(new PixelSize(1920, 1080), source.EncodeSize);
        Assert.True(source.UsingHardwareEncoder);
    }

    [Fact]
    public void NoFirstPicture_IsAFailure_ThatReleasesTheCapture()
    {
        var capture = new FakeScreenCapture();

        Assert.Throws<InvalidOperationException>(() => Source(capture));

        Assert.Empty(_built);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public void AChangedScreen_IsEncoded_AndHandedOut()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080).Frame(1920, 1080);
        var source = Source(capture);

        Assert.True(source.TryGetNextFrame(Tick, forceKeyframe: false, out var frame));

        Assert.Equal(1920, BitConverter.ToInt32(frame.Data));
        Assert.False(frame.IsKeyframe);
        var fed = Assert.Single(Encoder.Encoded);
        Assert.False(fed.ForceKeyframe);
    }

    // Rule 1: a tick where the screen did not change still asks for a picture the encoder held back.
    [Fact]
    public void AStillTick_StillTakesAPictureTheEncoderHeldBack()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080).Frame(1920, 1080).Still();
        _nextEncoder.Then(FakeVideoEncoder.Answer.HoldBack);
        var source = Source(capture);

        Assert.False(source.TryGetNextFrame(Tick, forceKeyframe: false, out _));
        Assert.True(source.TryGetNextFrame(Tick, forceKeyframe: false, out var late));

        Assert.Equal(1920, BitConverter.ToInt32(late.Data));
        Assert.Equal(1, Encoder.PendingCalls);
    }

    // Rule 2, first half: a still screen is normal.
    [Fact]
    public void AStillScreen_IsNoFrameAndNoError_HoweverLongItLasts()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080);
        var source = Source(capture);

        for (var tick = 0; tick < 100; tick++)
        {
            Assert.False(source.TryGetNextFrame(Tick, forceKeyframe: false, out _));
        }

        Assert.Empty(Encoder.Encoded);
        Assert.Equal(100, Encoder.PendingCalls);
    }

    [Fact]
    public void AForcedKeyframe_OnAStillScreen_EncodesTheLastPictureAgain()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080).Frame(1920, 1080).Still();
        var source = Source(capture);
        Assert.True(source.TryGetNextFrame(Tick, forceKeyframe: false, out _));

        Assert.True(source.TryGetNextFrame(Tick, forceKeyframe: true, out var frame));

        Assert.True(frame.IsKeyframe);
        Assert.Equal(2, Encoder.Encoded.Count);
        Assert.True(Encoder.Encoded[1].ForceKeyframe);
        Assert.Equal(1920, Encoder.Encoded[1].Frame.Width);
    }

    // Rule 2, second half: an encoder that keeps refusing while the screen is changing is stuck.
    [Fact]
    public void AnEncoderThatKeepsRefusing_WhileTheScreenChanges_IsAFailure()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080);
        for (var i = 0; i < CaptureEncodeSource.MaxConsecutiveRefusals; i++)
        {
            capture.Frame(1920, 1080);
        }

        _nextEncoder.Fallback = FakeVideoEncoder.Answer.Refuse;
        var source = Source(capture);

        for (var i = 1; i < CaptureEncodeSource.MaxConsecutiveRefusals; i++)
        {
            Assert.False(source.TryGetNextFrame(Tick, forceKeyframe: false, out _));
        }

        Assert.Throws<InvalidOperationException>(() => source.TryGetNextFrame(Tick, forceKeyframe: false, out _));
    }

    [Fact]
    public void APictureThatComesOut_ResetsTheRefusalCount()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080);
        for (var i = 0; i < 2 * CaptureEncodeSource.MaxConsecutiveRefusals; i++)
        {
            capture.Frame(1920, 1080);
        }

        for (var i = 1; i < CaptureEncodeSource.MaxConsecutiveRefusals; i++)
        {
            _nextEncoder.Then(FakeVideoEncoder.Answer.Refuse);
        }

        _nextEncoder.Then(FakeVideoEncoder.Answer.Return);
        _nextEncoder.Fallback = FakeVideoEncoder.Answer.Refuse;
        var source = Source(capture);

        // Four refusals, one picture, four more refusals: nine calls, none of them the failure.
        var calls = 2 * CaptureEncodeSource.MaxConsecutiveRefusals - 1;
        var produced = 0;
        for (var i = 0; i < calls; i++)
        {
            produced += source.TryGetNextFrame(Tick, forceKeyframe: false, out _) ? 1 : 0;
        }

        Assert.Equal(1, produced);
        Assert.Equal(calls, Encoder.Encoded.Count);
    }

    // Rule 4: the display changed size on air.
    [Fact]
    public void ADifferentCaptureSize_RebuildsTheEncoder_TellsTheOwner_AndStartsWithAKeyframe()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080).Frame(1280, 720).Frame(1280, 720);
        var source = Source(capture);
        var first = Encoder;
        var announced = new List<PixelSize>();
        source.EncodeSizeChanged += announced.Add;

        Assert.False(source.TryGetNextFrame(Tick, forceKeyframe: false, out _));

        Assert.Equal(2, _built.Count);
        Assert.Equal(new PixelSize(1280, 720), _built[1].Size);
        Assert.Equal(VideoBitrate.BitsPerSecond(new PixelSize(1280, 720), 15, Sharpness.Standard), _built[1].BitsPerSecond);
        Assert.True(first.Disposed);
        Assert.Empty(first.Encoded);
        Assert.Equal([new PixelSize(1280, 720)], announced);
        Assert.Equal(new PixelSize(1280, 720), source.EncodeSize);

        Assert.True(source.TryGetNextFrame(Tick, forceKeyframe: false, out var frame));
        Assert.True(frame.IsKeyframe);
        Assert.Equal(1280, Encoder.Encoded.Single().Frame.Width);
    }

    [Fact]
    public void ASmallerDisplay_IsNotScaledUp()
    {
        var capture = new FakeScreenCapture().Frame(1366, 768);

        var source = Source(capture, QualityPreset.Hd15);

        Assert.Equal(new PixelSize(1366, 768), source.EncodeSize);
    }

    [Fact]
    public void Dispose_ReleasesTheEncoderAndTheCapture()
    {
        var capture = new FakeScreenCapture().Frame(1920, 1080);
        var source = Source(capture);

        source.Dispose();

        Assert.True(Encoder.Disposed);
        Assert.True(capture.Disposed);
        Assert.Throws<ObjectDisposedException>(() => source.TryGetNextFrame(Tick, forceKeyframe: false, out _));
    }
}
