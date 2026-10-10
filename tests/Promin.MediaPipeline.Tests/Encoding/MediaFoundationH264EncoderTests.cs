using Promin.MediaPipeline.Capture;
using Promin.MediaPipeline.Encoding;
using Promin.Protocol.Video;
using Xunit.Abstractions;

namespace Promin.MediaPipeline.Tests.Encoding;

// These tests need the GPU and Media Foundation, and the first one CAPTURES THE REAL SCREEN of whoever is
// using the machine (one frame per case; it does not move the pointer and does not need a still desktop).
// They cannot run on a headless CI agent: run them by hand on the target machine (remove the Skip text,
// run, put it back).
public class MediaFoundationH264EncoderTests(ITestOutputHelper output)
{
    private const string NeedsScreen =
        "Requires the GPU and Media Foundation and captures the real screen; run manually on target hardware.";

    private const string NeedsGpu =
        "Requires the GPU and Media Foundation; uses synthetic pictures and captures nothing; run manually on target hardware.";

    // The encode sizes FrameSizing.EncodeSize produces for common displays. Given here as plain numbers,
    // not computed, so a sizing bug cannot hide an encoder bug. The capture is at the display's own size,
    // so the converter scales (or enlarges) it to each of these.
    [Theory(Skip = NeedsScreen)]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(1920, 802)]
    [InlineData(606, 1080)]
    [InlineData(1280, 718)]
    public void TryEncode_ProducesANonEmptyKeyframe_ForTheFirstCapturedFrame(int width, int height)
    {
        ScreenTestSupport.RequireDpiAwareness();
        using var capture = new DxgiScreenCapture();
        using var encoder = new MediaFoundationH264Encoder(capture.Device, width, height, targetFps: 15, Standard(width, height, 15));
        Describe(encoder);
        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out var frame), "the capture produced no frame within 5 s");

        // The first call carries the keyframe request. An encoder that holds a picture back is given the
        // same frame again (not forced) until its first picture comes out; that first picture is the one
        // that must be the keyframe.
        EncodedFrame first = default;
        var calls = 0;
        for (; calls < 6; calls++)
        {
            if (encoder.TryEncode(frame, forceKeyframe: calls == 0, out first))
                break;
        }

        output.WriteLine($"{width}x{height}: first picture after {calls + 1} call(s), {first.Data?.Length ?? 0} bytes, keyframe={first.IsKeyframe}");
        Assert.NotNull(first.Data);
        Assert.NotEmpty(first.Data);
        Assert.True(first.IsKeyframe);
        Assert.Equal(0u, first.FrameId);

        var summary = H264Stream.Inspect(first.Data);
        Assert.True(summary.IsAnnexB);
        Assert.True(summary.HasIdr);
        Assert.True(summary.HasParameterSets);

        Assert.True(H264Stream.TryReadFrameSize(first.Data, out var encodedWidth, out var encodedHeight), "no readable SPS in the keyframe");
        Assert.Equal((width, height), (encodedWidth, encodedHeight));
        output.WriteLine($"keyframes the encoder wrote without SPS/PPS and had them added: {encoder.KeyframesCompletedFromMemory}");
    }

    [Fact(Skip = NeedsGpu)]
    public void ReportsWhichEncoderThisMachineProvides()
    {
        using var gpu = new TestGpu();
        output.WriteLine($"adapter: {gpu.AdapterName}");
        output.WriteLine($"IsHardwareEncoderAvailable: {MediaFoundationH264Encoder.IsHardwareEncoderAvailable()}");

        using var encoder = new MediaFoundationH264Encoder(gpu.Device, 1280, 720, 15, Standard(1280, 720, 15));
        Describe(encoder);

        Assert.False(string.IsNullOrWhiteSpace(encoder.EncoderName));
        if (encoder.UsingHardwareEncoder)
            Assert.True(MediaFoundationH264Encoder.IsHardwareEncoderAvailable());
    }

    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void SyntheticFrames_FirstIsAKeyframe_TheRestAreNot_IdsHaveNoGaps(bool allowHardware)
    {
        using var gpu = new TestGpu();
        using var encoder = new MediaFoundationH264Encoder(gpu.Device, 1280, 720, 15, Standard(1280, 720, 15), allowHardware);
        Describe(encoder);

        var frames = new List<EncodedFrame>();
        for (var i = 0; i < 30; i++)
        {
            using var picture = gpu.MovingPattern(1280, 720, i);
            if (encoder.TryEncode(new CapturedFrame(picture, 1280, 720), forceKeyframe: i == 0, out var encoded))
                frames.Add(encoded);
        }

        output.WriteLine($"{frames.Count} of 30 calls produced a picture; sizes: {string.Join(", ", frames.Select(f => f.Data.Length))}");
        Assert.True(frames.Count >= 29, "an encoder set up for low delay should return a picture per call, or at worst one behind");
        Assert.True(frames[0].IsKeyframe);
        Assert.All(frames.Skip(1), f => Assert.False(f.IsKeyframe));
        Assert.Equal(Enumerable.Range(0, frames.Count).Select(i => (uint)i), frames.Select(f => f.FrameId));
        Assert.All(frames, f => Assert.NotEmpty(f.Data));
        AssertKeyframeIsDecodable(frames[0]);
    }

    // The session relies on a forced keyframe really being one, and on IsKeyframe reporting what the
    // encoder produced. Needs a picture back for every call so output i belongs to input i.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void SyntheticFrames_ForcedKeyframes_AreHonouredExactly(bool allowHardware)
    {
        using var gpu = new TestGpu();
        using var encoder = new MediaFoundationH264Encoder(gpu.Device, 1280, 720, 15, Standard(1280, 720, 15), allowHardware);
        Describe(encoder);

        // 120 pictures: far past anything a periodic keyframe of the encoder's own would wait for, so one that
        // was not asked for would fail the comparison below.
        var forced = new HashSet<int> { 0, 5, 10, 11, 19, 60, 100 };
        for (var i = 0; i < 120; i++)
        {
            using var picture = gpu.MovingPattern(1280, 720, i);
            var gotOne = encoder.TryEncode(new CapturedFrame(picture, 1280, 720), forceKeyframe: forced.Contains(i), out var encoded);

            Assert.True(gotOne, $"call {i} returned no picture: this encoder holds pictures back, which breaks the one-in one-out assumption");
            Assert.Equal(forced.Contains(i), encoded.IsKeyframe);
            Assert.Equal((uint)i, encoded.FrameId);
            if (forced.Contains(i))
                AssertKeyframeIsDecodable(encoded);
        }

        // Not asserted: either answer is acceptable. It says whether this encoder writes SPS/PPS into every
        // keyframe itself (0) or the later ones needed the remembered sets put in front (more than 0). There are
        // seven keyframes in this test, the first of which has always carried them.
        output.WriteLine($"keyframes in this run: {forced.Count}; keyframes the encoder wrote without SPS/PPS and had them added: {encoder.KeyframesCompletedFromMemory}");
    }

    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void SyntheticFrames_OfAnySize_AreScaledToTheEncodeSize(bool allowHardware)
    {
        using var gpu = new TestGpu();
        using var encoder = new MediaFoundationH264Encoder(gpu.Device, 640, 360, 10, Standard(640, 360, 10), allowHardware);
        Describe(encoder);

        var sizes = new[] { (1280, 720), (800, 600), (1920, 1080), (640, 360), (1366, 768) };
        for (var i = 0; i < sizes.Length; i++)
        {
            var (w, h) = sizes[i];
            using var picture = gpu.MovingPattern(w, h, i);
            // Every picture is forced to be a keyframe, so each one carries an SPS that says what size was encoded.
            Assert.True(encoder.TryEncode(new CapturedFrame(picture, w, h), forceKeyframe: true, out var encoded), $"{w}x{h} produced no picture");
            Assert.NotEmpty(encoded.Data);
            Assert.Equal((uint)i, encoded.FrameId);
            Assert.True(encoded.IsKeyframe);
            Assert.True(H264Stream.TryReadFrameSize(encoded.Data, out var encodedWidth, out var encodedHeight), $"{w}x{h}: no readable SPS");
            Assert.Equal((640, 360), (encodedWidth, encodedHeight));
        }
    }

    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void TryTakePending_WithNothingHeld_ReturnsFalseAtOnce_AndAfterAnEncode(bool allowHardware)
    {
        using var gpu = new TestGpu();
        using var encoder = new MediaFoundationH264Encoder(gpu.Device, 640, 360, 10, Standard(640, 360, 10), allowHardware);
        Describe(encoder);
        using var picture = gpu.MovingPattern(640, 360, 0);

        Assert.False(encoder.TryTakePending(out _)); // before anything was fed
        Assert.True(encoder.TryEncode(new CapturedFrame(picture, 640, 360), forceKeyframe: true, out _));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var held = encoder.TryTakePending(out _);
        clock.Stop();
        output.WriteLine($"TryTakePending after one picture: {held}, {clock.Elapsed.TotalMilliseconds:F1} ms");

        Assert.False(held);
        Assert.True(clock.ElapsedMilliseconds < 50);
    }

    [Fact(Skip = NeedsGpu)]
    public void Dispose_CanBeCalledTwice_AndTryEncodeAfterItThrows()
    {
        using var gpu = new TestGpu();
        var encoder = new MediaFoundationH264Encoder(gpu.Device, 640, 360, 10, Standard(640, 360, 10));
        using var picture = gpu.MovingPattern(640, 360, 0);

        encoder.Dispose();
        encoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => encoder.TryEncode(new CapturedFrame(picture, 640, 360), false, out _));
    }

    private static int Standard(int width, int height, int fps) =>
        VideoBitrate.BitsPerSecond(new PixelSize(width, height), fps, Sharpness.Standard);

    private void Describe(MediaFoundationH264Encoder encoder)
    {
        output.WriteLine($"bitrate: {encoder.BitsPerSecond}");
        output.WriteLine($"encoder: {encoder.EncoderName}");
        output.WriteLine($"hardware: {encoder.UsingHardwareEncoder}, asynchronous: {encoder.IsAsynchronous}, GPU input: {encoder.UsesGpuInput}");
        output.WriteLine($"size: {encoder.Width}x{encoder.Height}");
        output.WriteLine($"configuration: {encoder.Configuration}");
    }

    private static void AssertKeyframeIsDecodable(EncodedFrame frame)
    {
        var summary = H264Stream.Inspect(frame.Data);
        Assert.True(summary.IsAnnexB, "not an Annex B byte stream");
        Assert.True(summary.HasIdr, "no IDR slice");
        Assert.True(summary.HasParameterSets, "no SPS/PPS: a student joining at this keyframe could not decode it");
    }
}
