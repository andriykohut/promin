using System.Diagnostics;
using Educast.MediaPipeline.Capture;
using Educast.MediaPipeline.Decoding;
using Educast.MediaPipeline.Encoding;
using Educast.MediaPipeline.Tests.Encoding;
using Educast.Protocol.Video;
using Xunit.Abstractions;

namespace Educast.MediaPipeline.Tests.Decoding;

// Most of these tests need the GPU and Media Foundation; they use synthetic pictures (the real encoder encodes a
// generated texture, the decoder decodes it) and capture nothing. Only the two marked NeedsScreen capture the
// REAL SCREEN of whoever is using the machine (one frame each; the pointer is not moved). None can run on a
// headless CI agent: run them by hand on the target machine (remove the Skip text, run, put it back).
// "hardware" below means the decoder is given the Direct3D device (it then decodes on the GPU where it can);
// "software" means it is not, so the same decoder runs in memory and the picture is uploaded.
public class MediaFoundationH264DecoderTests(ITestOutputHelper output)
{
    private const string NeedsScreen =
        "Requires the GPU and Media Foundation and captures the real screen; run manually on target hardware.";

    private const string NeedsGpu =
        "Requires the GPU and Media Foundation; uses synthetic pictures and captures nothing; run manually on target hardware.";

    private readonly record struct Colour(byte Blue, byte Green, byte Red);

    // ---- offline ----------------------------------------------------------------------------

    [Fact]
    public void Constructor_RefusesANullDevice() =>
        Assert.Throws<ArgumentNullException>(() => new MediaFoundationH264Decoder(null!));

    // ---- what this machine provides --------------------------------------------------------

    [Fact(Skip = NeedsGpu)]
    public void ReportsWhichDecoderThisMachineProvides()
    {
        using var gpu = new TestGpu();
        output.WriteLine($"adapter: {gpu.AdapterName}");
        using var decoder = new MediaFoundationH264Decoder(gpu.Device);
        Describe(decoder, "before the first picture");

        var key = Encode(gpu, 1280, 720, [new Colour(128, 128, 128)])[0];
        Assert.True(decoder.TryDecode(key, out var frame));
        Describe(decoder, "after the first picture");
        output.WriteLine($"texture {frame.Texture.Description.Width}x{frame.Texture.Description.Height} {frame.Texture.Description.Format}, picture {frame.Width}x{frame.Height}");

        Assert.False(string.IsNullOrWhiteSpace(decoder.DecoderName));
        Assert.False(decoder.IsAsynchronous);
        Assert.Equal(0, decoder.PicturesHeldBack);
        Assert.NotNull(decoder.IsHardwareAccelerated);
    }

    // ---- sizes that are not 1080p ---------------------------------------------------------------

    // The sizes FrameSizing.EncodeSize produces for common displays, as plain numbers. The picture's size is
    // what is reported, not the coded size (1920x1088, 1920x816, ...): the corners of the picture are checked
    // too, so a decoder that reported the right size for the wrong region would be caught.
    [Theory(Skip = NeedsGpu)]
    [InlineData(1920, 1080, true)]
    [InlineData(1366, 768, true)]
    [InlineData(1920, 802, true)]
    [InlineData(606, 1080, true)]
    [InlineData(1920, 1080, false)]
    [InlineData(1366, 768, false)]
    [InlineData(1920, 802, false)]
    [InlineData(606, 1080, false)]
    public void SyntheticKeyframe_IsReportedAtThePicturesSize_AndItsCornersAreDecoded(int width, int height, bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var key = Encode(gpu, width, height, [new Colour(128, 128, 128)])[0];
        Assert.True(H264Stream.TryReadFrameSize(key.Data, out var spsWidth, out var spsHeight));
        Assert.Equal((width, height), (spsWidth, spsHeight));

        Assert.True(decoder.TryDecode(key, out var frame), decoder.LastFailure);
        Describe(decoder, $"{width}x{height}");

        Assert.Equal((width, height), (frame.Width, frame.Height));
        var texture = frame.Texture.Description;
        Assert.Equal(Vortice.DXGI.Format.NV12, texture.Format);
        Assert.True(texture.Width >= (uint)width && texture.Height >= (uint)height);
        output.WriteLine($"texture {texture.Width}x{texture.Height}, picture {frame.Width}x{frame.Height}");

        var nv12 = gpu.ReadNv12(frame.Texture, (int)texture.Width, (int)texture.Height);
        var expected = 16 + 219 * 128 / 255; // limited-range luma of mid grey
        foreach (var (x, y) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1), (width / 2, height / 2) })
            Assert.InRange(nv12[y * (int)texture.Width + x], expected - 5, expected + 5);
    }

    // The size comes from the stream and a change of size is followed: each keyframe comes from a fresh encoder
    // at another size, through the same decoder; the first picture after each change reports the new size, and
    // the stream carries on at it.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void SizeChange_MidStream_IsFollowed(bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);

        var sizes = new[] { (1920, 1080), (1366, 768), (1920, 802), (606, 1080), (1920, 1080), (606, 1080) };
        foreach (var (width, height) in sizes)
        {
            var frames = Encode(gpu, width, height, [new Colour(128, 128, 128), new Colour(40, 40, 40)]);
            Assert.True(frames[0].IsKeyframe);

            Assert.True(decoder.TryDecode(frames[0], out var frame), $"{width}x{height}: {decoder.LastFailure}");
            Assert.Equal((width, height), (frame.Width, frame.Height));

            Assert.True(decoder.TryDecode(frames[1], out var next), $"{width}x{height} delta: {decoder.LastFailure}");
            Assert.Equal((width, height), (next.Width, next.Height));
            var texture = next.Texture.Description;
            var nv12 = gpu.ReadNv12(next.Texture, (int)texture.Width, (int)texture.Height);
            Assert.InRange(nv12[(height / 2) * (int)texture.Width + width / 2], 16 + 219 * 40 / 255 - 6, 16 + 219 * 40 / 255 + 6);
            output.WriteLine($"{width}x{height}: ok (texture {texture.Width}x{texture.Height})");
        }
        Describe(decoder, "after the size changes");
    }

    // ---- low delay -------------------------------------------------------------------------------

    // The requirement: a picture comes back from the call that fed it. The pictures are solid greys that jump
    // about, so a decoder that returned the previous picture (one behind) would be caught by the colour, not
    // just by a count. A keyframe is forced in the middle of the stream.
    // Run against the streams of the hardware encoder and of the software encoder, since a decoder may treat
    // two encoders' streams differently.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ThirtyFrames_EachPictureComesBackFromTheCallThatFedIt(bool hardware, bool hardwareEncoder)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var colours = Enumerable.Range(0, 30).Select(i => Grey(i)).ToList();
        var frames = Encode(gpu, 1280, 720, colours, keyframes: [15], hardwareEncoder: hardwareEncoder);
        Assert.True(frames[15].IsKeyframe);

        var times = new List<double>();
        for (var i = 0; i < frames.Count; i++)
        {
            var clock = Stopwatch.StartNew();
            var decoded = decoder.TryDecode(frames[i], out var frame);
            clock.Stop();
            times.Add(clock.Elapsed.TotalMilliseconds);

            Assert.True(decoded, $"call {i} returned no picture: this decoder holds pictures back ({decoder.LastFailure})");
            Assert.Equal((1280, 720), (frame.Width, frame.Height));

            var texture = frame.Texture.Description;
            var nv12 = gpu.ReadNv12(frame.Texture, (int)texture.Width, (int)texture.Height);
            var expected = 16 + 219.0 * colours[i].Green / 255;
            var actual = nv12[360 * (int)texture.Width + 640];
            Assert.True(Math.Abs(actual - expected) <= 5, $"call {i}: luma {actual}, expected about {expected:F0} (the previous picture's was {16 + 219.0 * colours[Math.Max(i - 1, 0)].Green / 255:F0})");
        }

        Assert.Equal(0, decoder.PicturesHeldBack);
        Assert.False(decoder.NeedsKeyframe);
        output.WriteLine($"decode time per call: first {times[0]:F1} ms, then min {times.Skip(1).Min():F1} / avg {times.Skip(1).Average():F1} / max {times.Skip(1).Max():F1} ms");
        Describe(decoder, "after 30 frames");
    }

    // The session calls the decoder from whichever thread is free, one call at a time. Every call here runs on
    // its own new thread (started and joined before the next), the decoder is created on one thread and
    // disposed on yet another, and the test asserts that more than one thread really was used.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void CallsFromDifferentThreads_OneAtATime_AreOneInOneOut(bool hardware)
    {
        using var gpu = new TestGpu();
        var colours = Enumerable.Range(0, 12).Select(i => Grey(i)).ToList();
        var frames = Encode(gpu, 640, 360, colours);
        var threads = new List<Thread>(); // kept alive so no managed thread id can be reused

        var decoder = OnNewThread(threads, () => new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware), out var createdOn);
        var decodedOn = new HashSet<int>();
        var disposedOn = createdOn;
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var index = i;
                var (decoded, luma, failure) = OnNewThread(threads, () =>
                {
                    var ok = decoder.TryDecode(frames[index], out var frame);
                    var texture = ok ? frame.Texture.Description : default;
                    var nv12 = ok ? gpu.ReadNv12(frame.Texture, (int)texture.Width, (int)texture.Height) : Array.Empty<byte>();
                    return (ok, ok ? nv12[180 * (int)texture.Width + 320] : (byte)0, decoder.LastFailure);
                }, out var thread);
                decodedOn.Add(thread);

                Assert.True(decoded, $"call {i}: {failure}");
                Assert.InRange(luma, 16 + 219.0 * colours[i].Green / 255 - 5, 16 + 219.0 * colours[i].Green / 255 + 5);
            }
            Assert.Equal(0, decoder.PicturesHeldBack);
        }
        finally
        {
            OnNewThread(threads, () => { decoder.Dispose(); return 0; }, out disposedOn);
            output.WriteLine($"created on thread {createdOn}, {frames.Count} calls on {decodedOn.Count} threads, disposed on thread {disposedOn}");
        }

        Assert.True(decodedOn.Count > 1, "the calls did not run on more than one thread");
        Assert.DoesNotContain(createdOn, decodedOn);
        Assert.NotEqual(createdOn, disposedOn);
    }

    // ---- hostile input, long run ----------------------------------------------------------------------

    // Access units with well-formed SPS, PPS and IDR headers over junk payload: they pass every gate of the
    // pipeline and reach the real transform. Whatever the transform does (refuse, fail, conceal), TryDecode must
    // not throw, and the next good keyframe must decode correctly. Fed before the stream starts and in the middle of it.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void WellFramedJunk_ReachesTheTransform_ThrowsNothing_AndTheNextGoodKeyframeDecodes(bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var frames = Encode(gpu, 640, 360, [Grey(1), Grey(2), Grey(3)], keyframes: [1, 2]);
        var random = new Random(99);

        var before = decoder.TryDecode(new EncodedFrame(WellFramedJunk(random), true, 0), out _);
        output.WriteLine($"junk before the stream: returned {before}, failure: {decoder.LastFailure}, needs keyframe: {decoder.NeedsKeyframe}, held back: {decoder.PicturesHeldBack}");

        Assert.True(decoder.TryDecode(frames[0], out _), decoder.LastFailure);
        var during = decoder.TryDecode(new EncodedFrame(WellFramedJunk(random), true, 1), out _);
        output.WriteLine($"junk mid-stream: returned {during}, failure: {decoder.LastFailure}, needs keyframe: {decoder.NeedsKeyframe}");
        var junkDelta = decoder.TryDecode(new EncodedFrame([0, 0, 0, 1, 0x41, .. RandomBytes(random, 2000)], false, 2), out _);
        output.WriteLine($"junk slice mid-stream: returned {junkDelta}, failure: {decoder.LastFailure}, needs keyframe: {decoder.NeedsKeyframe}");

        Assert.True(decoder.TryDecode(frames[2], out var frame), decoder.LastFailure);
        AssertGrey(gpu, frame, 640, 360, Grey(3));
        Assert.False(decoder.NeedsKeyframe);
    }

    // For a two-hour lesson: 3,000 frames through one decoder in each mode (a keyframe every 300), with the
    // process memory read before and after and REPORTED; the assertion is only a gross sanity bound.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void ThreeThousandFrames_OneDecoder_AreOneInOneOut_AndMemoryIsReported(bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        using var encoder = new MediaFoundationH264Encoder(gpu.Device, 1280, 720, 15, Standard(1280, 720, 15));
        const int count = 3000;
        const int warmup = 100;
        long privateBefore = 0, workingBefore = 0, managedBefore = 0;
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
        {
            if (i == warmup)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var process = Process.GetCurrentProcess();
                (privateBefore, workingBefore, managedBefore) = (process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(true));
            }

            var colour = Grey(i);
            using var picture = gpu.SolidTexture(1280, 720, colour.Blue, colour.Green, colour.Red);
            Assert.True(encoder.TryEncode(new CapturedFrame(picture, 1280, 720), forceKeyframe: i % 300 == 0, out var encoded), $"the encoder returned no picture for input {i}");

            Assert.True(decoder.TryDecode(encoded, out var frame), $"call {i} returned no picture ({decoder.LastFailure})");
            Assert.Equal((1280, 720), (frame.Width, frame.Height));
            if (i % 100 == 0)
                AssertGrey(gpu, frame, 1280, 720, colour);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var after = Process.GetCurrentProcess();
        var mb = 1024.0 * 1024;
        output.WriteLine($"{count} frames in {clock.Elapsed.TotalSeconds:F1} s ({(hardware ? "hardware" : "software")} decode)");
        output.WriteLine($"private bytes {privateBefore / mb:F0} MB -> {after.PrivateMemorySize64 / mb:F0} MB; working set {workingBefore / mb:F0} MB -> {after.WorkingSet64 / mb:F0} MB; managed {managedBefore / mb:F1} MB -> {GC.GetTotalMemory(true) / mb:F1} MB (measured after {warmup} frames and after {count})");
        Describe(decoder, $"after {count} frames");

        Assert.Equal(0, decoder.PicturesHeldBack);
        Assert.True(after.PrivateMemorySize64 - privateBefore < 500 * mb, "private memory grew by more than 500 MB over 2,900 frames");
    }

    // ---- the picture is right ------------------------------------------------------------------------

    // The same colours the converter's own test checks, through encode and decode: the decoded NV12 is limited
    // range BT.709, so black is 16 and white 235, and red tells BT.709 from BT.601 apart.
    [Theory(Skip = NeedsGpu)]
    [InlineData(0, 0, 0, 16, 128, 128, true)]
    [InlineData(255, 255, 255, 235, 128, 128, true)]
    [InlineData(255, 0, 0, 63, 102, 240, true)]
    [InlineData(0, 0, 255, 32, 240, 118, true)]
    [InlineData(255, 0, 0, 63, 102, 240, false)]
    [InlineData(0, 0, 255, 32, 240, 118, false)]
    public void SolidColours_DecodeToTheExpectedNv12Values(byte red, byte green, byte blue, int y, int cb, int cr, bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var key = Encode(gpu, 640, 360, [new Colour(blue, green, red)])[0];

        Assert.True(decoder.TryDecode(key, out var frame), decoder.LastFailure);

        var texture = frame.Texture.Description;
        var width = (int)texture.Width;
        var nv12 = gpu.ReadNv12(frame.Texture, width, (int)texture.Height);
        var luma = nv12[180 * width + 320];
        var chromaRow = (int)texture.Height + 90; // the chroma plane starts after the luma plane's last row
        var cbValue = nv12[chromaRow * width + 320];
        var crValue = nv12[chromaRow * width + 321];
        output.WriteLine($"rgb({red},{green},{blue}) -> Y={luma} Cb={cbValue} Cr={crValue}; expected about Y={y} Cb={cb} Cr={cr}");

        Assert.InRange(luma, y - 4, y + 4);
        Assert.InRange(cbValue, cb - 6, cb + 6);
        Assert.InRange(crValue, cr - 6, cr + 6);
    }

    // ---- starting, and bytes that are not H.264 -------------------------------------------------------

    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void ANonKeyframeFirst_ReturnsFalse_ThrowsNothing_AndTheNextKeyframeStartsTheStream(bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var frames = Encode(gpu, 640, 360, [Grey(1), Grey(2), Grey(3)], keyframes: [2]);
        Assert.False(frames[1].IsKeyframe);
        Assert.True(frames[2].IsKeyframe);

        Assert.False(decoder.TryDecode(frames[1], out var none));
        Assert.Null(none.Texture);
        output.WriteLine($"first call with a delta: {decoder.LastFailure}");
        Assert.NotNull(decoder.LastFailure);

        Assert.True(decoder.TryDecode(frames[2], out var frame), decoder.LastFailure);
        Assert.Equal((640, 360), (frame.Width, frame.Height));
        Assert.Null(decoder.LastFailure);
    }

    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void BytesThatAreNotH264_ReturnFalse_ThrowNothing_AndTheDecoderStaysUsable(bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var frames = Encode(gpu, 640, 360, [Grey(1), Grey(2), Grey(3)], keyframes: [2]);
        var random = new Random(1234);

        // Before the stream started: no start code at all; and a start code with junk after it.
        Assert.False(decoder.TryDecode(new EncodedFrame(RandomBytes(random, 3000), true, 0), out _));
        Assert.False(decoder.TryDecode(new EncodedFrame([0, 0, 0, 1, 0x65, .. RandomBytes(random, 3000)], true, 1), out _));
        Assert.False(decoder.TryDecode(new EncodedFrame([], true, 2), out _));

        // The real stream starts, and then garbage arrives in the middle of it.
        Assert.True(decoder.TryDecode(frames[0], out _), decoder.LastFailure);
        Assert.False(decoder.TryDecode(new EncodedFrame(RandomBytes(random, 3000), false, 3), out _));
        output.WriteLine($"after garbage in mid-stream: {decoder.LastFailure}");

        // The stream is broken until the next keyframe; a delta in between is refused ...
        Assert.False(decoder.TryDecode(frames[1], out _));
        // ... and the keyframe picks it up again, with the right picture.
        Assert.True(decoder.TryDecode(frames[2], out var frame), decoder.LastFailure);
        AssertGrey(gpu, frame, 640, 360, Grey(3));
    }

    // A keyframe whose slice data is damaged (as a lost or corrupted packet would leave it) may decode to a
    // smeared picture or be refused: either is allowed, throwing is not, and the next good keyframe must decode.
    [Theory(Skip = NeedsGpu)]
    [InlineData(true)]
    [InlineData(false)]
    public void ADamagedKeyframe_ThrowsNothing_AndTheNextGoodKeyframeDecodesCorrectly(bool hardware)
    {
        using var gpu = new TestGpu();
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardware);
        var frames = Encode(gpu, 640, 360, [Grey(1), Grey(2), Grey(3)], keyframes: [1, 2]);

        var damaged = frames[1].Data.ToArray();
        for (var i = damaged.Length / 3; i < damaged.Length * 2 / 3; i++)
            damaged[i] = 0xA5;
        var truncated = frames[2].Data[..(frames[2].Data.Length / 2)];

        Assert.True(decoder.TryDecode(frames[0], out _), decoder.LastFailure);
        var damagedResult = decoder.TryDecode(new EncodedFrame(damaged, true, 1), out _);
        output.WriteLine($"damaged keyframe: returned {damagedResult}, last failure: {decoder.LastFailure}");
        var truncatedResult = decoder.TryDecode(new EncodedFrame(truncated, true, 2), out _);
        output.WriteLine($"truncated keyframe: returned {truncatedResult}, last failure: {decoder.LastFailure}");

        Assert.True(decoder.TryDecode(frames[2], out var frame), decoder.LastFailure);
        AssertGrey(gpu, frame, 640, 360, Grey(3));
    }

    // ---- dispose ------------------------------------------------------------------------------------

    [Fact(Skip = NeedsGpu)]
    public void Dispose_CanBeCalledTwice_AndTryDecodeAfterItThrows()
    {
        using var gpu = new TestGpu();
        var decoder = new MediaFoundationH264Decoder(gpu.Device);
        var key = Encode(gpu, 640, 360, [Grey(1)])[0];
        Assert.True(decoder.TryDecode(key, out _));

        decoder.Dispose();
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.TryDecode(key, out _));
    }

    [Fact(Skip = NeedsGpu)]
    public void ManyDecodersInTurn_OnOneDevice_DoNotLeakOrCrash()
    {
        using var gpu = new TestGpu();
        var key = Encode(gpu, 640, 360, [Grey(1)])[0];

        for (var i = 0; i < 20; i++)
        {
            using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: i % 2 == 0);
            Assert.True(decoder.TryDecode(key, out var frame), $"decoder {i}: {decoder.LastFailure}");
            Assert.Equal((640, 360), (frame.Width, frame.Height));
        }
    }

    // ---- the real screen, as the brief's round trip ------------------------------------------------------

    // Encodes one keyframe of the real screen on the capture's own device at each encode size and decodes it on
    // the same device; the decoded size must equal the encode size exactly.
    [Theory(Skip = NeedsScreen)]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(1920, 802)]
    [InlineData(606, 1080)]
    public void TryDecode_OfARealScreenKeyframe_ReportsTheEncodeSize(int width, int height)
    {
        ScreenTestSupport.RequireDpiAwareness();
        using var capture = new DxgiScreenCapture();
        using var encoder = new MediaFoundationH264Encoder(capture.Device, width, height, 15, Standard(width, height, 15));
        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out var captured), "the capture produced no frame within 5 s");
        Assert.True(encoder.TryEncode(captured, forceKeyframe: true, out var encoded));

        using var decoder = new MediaFoundationH264Decoder(capture.Device);
        Assert.True(decoder.TryDecode(encoded, out var frame), decoder.LastFailure);
        Describe(decoder, $"{width}x{height}");

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
    }

    [Fact(Skip = NeedsScreen)]
    public void SizeChange_MidStream_IsFollowed_ForARealScreenKeyframe()
    {
        ScreenTestSupport.RequireDpiAwareness();
        using var capture = new DxgiScreenCapture();
        using var first = new MediaFoundationH264Encoder(capture.Device, 1920, 1080, 15, Standard(1920, 1080, 15));
        using var second = new MediaFoundationH264Encoder(capture.Device, 1366, 768, 15, Standard(1366, 768, 15));
        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out var captured), "the capture produced no frame within 5 s");
        Assert.True(first.TryEncode(captured, forceKeyframe: true, out var firstKey));
        Assert.True(second.TryEncode(captured, forceKeyframe: true, out var secondKey));

        using var decoder = new MediaFoundationH264Decoder(capture.Device);
        Assert.True(decoder.TryDecode(firstKey, out var frame), decoder.LastFailure);
        Assert.Equal((1920, 1080), (frame.Width, frame.Height));

        Assert.True(decoder.TryDecode(secondKey, out frame), decoder.LastFailure);
        Assert.Equal((1366, 768), (frame.Width, frame.Height));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static int Standard(int width, int height, int fps) =>
        VideoBitrate.BitsPerSecond(new PixelSize(width, height), fps, Sharpness.Standard);

    // Greys whose brightness jumps about (differences of at least 90 levels from one picture to the next in
    // nearly every case), so that a picture from the wrong call cannot pass for the right one.
    private static Colour Grey(int index)
    {
        var level = (byte)((index * 97) % 240 + 8);
        return new Colour(level, level, level);
    }

    // SPS, PPS and IDR slice headers (the NAL types are right, so every gate passes) followed by junk.
    private static byte[] WellFramedJunk(Random random) =>
    [
        0, 0, 0, 1, 0x67, .. RandomBytes(random, 24),
        0, 0, 0, 1, 0x68, .. RandomBytes(random, 6),
        0, 0, 0, 1, 0x65, .. RandomBytes(random, 4000),
    ];

    // Runs the function on a new thread and waits for it; the thread is kept in the list.
    private static T OnNewThread<T>(List<Thread> keep, Func<T> work, out int threadId)
    {
        T result = default!;
        Exception? failure = null;
        var id = 0;
        var thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        keep.Add(thread);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        threadId = id;
        return result;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        // No accidental start code, so these are not H.264 however they are read.
        for (var i = 2; i < bytes.Length; i++)
        {
            if (bytes[i] == 1 && bytes[i - 1] == 0 && bytes[i - 2] == 0)
                bytes[i] = 2;
        }
        return bytes;
    }

    /// <summary>Encodes one solid-colour picture per entry, the first as a keyframe (and any index in <paramref name="keyframes"/>), with the real encoder.</summary>
    private static List<EncodedFrame> Encode(TestGpu gpu, int width, int height, IReadOnlyList<Colour> colours, IReadOnlyCollection<int>? keyframes = null, bool hardwareEncoder = true)
    {
        using var encoder = new MediaFoundationH264Encoder(gpu.Device, width, height, 15, Standard(width, height, 15), hardwareEncoder);
        var frames = new List<EncodedFrame>();
        for (var i = 0; i < colours.Count; i++)
        {
            using var picture = gpu.SolidTexture(width, height, colours[i].Blue, colours[i].Green, colours[i].Red);
            var force = i == 0 || (keyframes?.Contains(i) ?? false);
            Assert.True(encoder.TryEncode(new CapturedFrame(picture, width, height), force, out var encoded), $"the encoder returned no picture for input {i}");
            frames.Add(encoded);
        }
        return frames;
    }

    private static void AssertGrey(TestGpu gpu, DecodedFrame frame, int width, int height, Colour grey)
    {
        Assert.Equal((width, height), (frame.Width, frame.Height));
        var texture = frame.Texture.Description;
        var nv12 = gpu.ReadNv12(frame.Texture, (int)texture.Width, (int)texture.Height);
        var expected = 16 + 219.0 * grey.Green / 255;
        var actual = nv12[(height / 2) * (int)texture.Width + width / 2];
        Assert.True(Math.Abs(actual - expected) <= 5, $"luma {actual}, expected about {expected:F0}");
    }

    private void Describe(MediaFoundationH264Decoder decoder, string when)
    {
        output.WriteLine($"[{when}] decoder: {decoder.DecoderName}");
        output.WriteLine($"[{when}] hardware accelerated: {decoder.IsHardwareAccelerated?.ToString() ?? "not known yet"}, asynchronous: {decoder.IsAsynchronous}, pictures held back: {decoder.PicturesHeldBack}, needs keyframe: {decoder.NeedsKeyframe}");
        output.WriteLine($"[{when}] configuration: {decoder.Configuration}");
    }
}
