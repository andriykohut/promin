using Promin.MediaPipeline.Capture;
using Promin.MediaPipeline.Encoding;
using Promin.Protocol.Messages;
using Promin.Protocol.Video;
using Xunit.Abstractions;
using static Promin.Agent.Tests.Hardware.GpuPictures;

namespace Promin.Agent.Tests.Hardware;

// These open a REAL WINDOW (windowed, 960x540, for a few seconds) on the screen of whoever is using the machine,
// and need the GPU, the video processor and Media Foundation. They capture nothing, move no mouse and send no
// keys. Run them by hand on the target machine, with the person looking (remove the Skip text, run, put it
// back). What they show: a window whose picture changes colour about every half second, black bars to the left
// and right of it (the pictures are 4:3, the window 16:9), and no pointer over it.
public class BroadcastDisplayTests(ITestOutputHelper output)
{
    private const string NeedsWindow =
        "Opens a real window for a few seconds and needs the GPU and Media Foundation; run manually on target hardware.";

    private static readonly Pixel[] Colours = [new(0, 0, 255), new(0, 255, 0), new(255, 0, 0), new(0, 200, 255)];

    private const int FramesPerColour = 8;

    /// <summary>Real encoded pictures: a keyframe at the start of each colour, deltas after it. Ids count from 0.</summary>
    private static List<byte[]> EncodedBroadcast(int width, int height, int colours)
    {
        using var gpu = GpuDevice.Create();
        using var encoder = new MediaFoundationH264Encoder(
            gpu.Device, width, height, 15, VideoBitrate.BitsPerSecond(new PixelSize(width, height), 15, Sharpness.Standard));

        var payloads = new List<byte[]>();
        for (var i = 0; i < colours * FramesPerColour; i++)
        {
            using var picture = Solid(gpu.Device, width, height, Colours[i / FramesPerColour % Colours.Length]);
            Assert.True(
                encoder.TryEncode(new CapturedFrame(picture, width, height), forceKeyframe: i % FramesPerColour == 0, out var encoded),
                $"the encoder returned no picture for input {i}");

            var payload = new byte[VideoFrameEnvelope.Size + encoded.Data.Length];
            new VideoFrameEnvelope((uint)i, encoded.IsKeyframe).WriteTo(payload);
            encoded.Data.CopyTo(payload, VideoFrameEnvelope.Size);
            payloads.Add(payload);
        }

        return payloads;
    }

    private static async Task<List<string>> ShowAsync(IReadOnlyList<byte[]> payloads, Action<string> echo)
    {
        var log = new List<string>();
        void Log(string line)
        {
            lock (log)
            {
                log.Add(line);
            }

            echo(line);
        }

        var display = await BroadcastDisplay.StartAsync(new StartBroadcast(1024, 768, 15), windowed: true, Log);
        Assert.NotNull(display);
        try
        {
            Assert.True(display.IsOpen);
            foreach (var payload in payloads)
            {
                await display.SubmitFrameAsync(payload, CancellationToken.None);
                await Task.Delay(66);
            }

            await Task.Delay(500); // the last picture stays up for a moment
        }
        finally
        {
            display.Dispose();
        }

        Assert.False(display.IsOpen);
        lock (log)
        {
            return [.. log];
        }
    }

    /// <summary>The numbers in the display's closing line, which says what became of every frame.</summary>
    private readonly record struct Summary(int Shown, int Dropped, int Discarded, int Refused, int Skipped)
    {
        public static Summary Parse(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                line,
                @"Pictures shown: (\d+), frames dropped: (\d+), discarded while waiting for a keyframe: (\d+), " +
                @"refused by the decoder: (\d+), decoded but not shown to stay current: (\d+)");
            Assert.True(m.Success, $"not the closing line: {line}");
            int N(int i) => int.Parse(m.Groups[i].Value);
            return new Summary(N(1), N(2), N(3), N(4), N(5));
        }

        public int Decoded => Shown + Skipped;
    }

    [Fact(Skip = NeedsWindow)]
    public async Task AWindowedDisplay_ShowsRealEncodedPictures_AndEndsCleanly()
    {
        var payloads = EncodedBroadcast(1024, 768, colours: 4);

        var log = await ShowAsync(payloads, output.WriteLine);

        Assert.Contains(log, line => line.Contains("The first picture is on screen"));
        var summary = Summary.Parse(Assert.Single(log, line => line.Contains("The display ended.")));
        Assert.Equal(payloads.Count, summary.Decoded); // every frame was decoded; some pictures may have been skipped to stay current
        Assert.True(summary.Shown >= payloads.Count / 2, $"only {summary.Shown} of {payloads.Count} pictures were shown");
        Assert.Equal(0, summary.Refused);
        Assert.Equal(0, summary.Discarded);
        Assert.DoesNotContain(log, line => line.Contains("failed"));
    }

    /// <summary>
    /// Teardown while frames are still arriving: it must not hang and must not throw. It is only worth anything
    /// if the display was working when it was removed, so the test first waits for a picture to be on screen
    /// and afterwards checks that nothing failed.
    /// </summary>
    [Fact(Skip = NeedsWindow)]
    public async Task ADisplayRemovedWhileFramesAreStillArriving_EndsWithoutHangingOrThrowing()
    {
        var payloads = EncodedBroadcast(1024, 768, colours: 2);
        var log = new List<string>();
        void Log(string line)
        {
            lock (log)
            {
                log.Add(line);
            }

            output.WriteLine(line);
        }

        bool Logged(string text)
        {
            lock (log)
            {
                return log.Any(line => line.Contains(text));
            }
        }

        var display = await BroadcastDisplay.StartAsync(new StartBroadcast(1024, 768, 15), windowed: true, Log);
        Assert.NotNull(display);

        var feeding = Task.Run(async () =>
        {
            foreach (var payload in payloads)
            {
                await display.SubmitFrameAsync(payload, CancellationToken.None); // never faults, whatever the display is doing
                await Task.Delay(20);
            }
        });

        for (var i = 0; i < 500 && !Logged("The first picture is on screen") && !Logged("failed"); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(Logged("The first picture is on screen"), "no picture was ever shown, so there was nothing to remove");
        Assert.True(display.IsOpen);

        var stopped = Task.Run(display.Dispose);

        await stopped.WaitAsync(TimeSpan.FromSeconds(15));
        await feeding.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(display.IsOpen);
        Assert.False(Logged("failed"), "the display failed");
        Assert.True(Summary.Parse(log.Single(line => line.Contains("The display ended."))).Shown >= 1);
    }

    [Fact(Skip = NeedsWindow)]
    public async Task ABroadcastThatStartsAgain_IsShownFromItsOwnFirstKeyframe()
    {
        var payloads = EncodedBroadcast(1024, 768, colours: 2);
        var log = new List<string>();
        var display = await BroadcastDisplay.StartAsync(
            new StartBroadcast(1024, 768, 15), windowed: true, line => { lock (log) { log.Add(line); } output.WriteLine(line); });
        Assert.NotNull(display);

        try
        {
            foreach (var payload in payloads.Take(FramesPerColour + 3)) // ids 0..10
            {
                await display.SubmitFrameAsync(payload, CancellationToken.None);
            }

            await display.RestartAsync(CancellationToken.None);
            foreach (var payload in payloads.Take(FramesPerColour)) // ids 0..7 again, as a restarted master counts
            {
                await display.SubmitFrameAsync(payload, CancellationToken.None);
            }

            await Task.Delay(500);
        }
        finally
        {
            display.Dispose();
        }

        Summary summary;
        lock (log)
        {
            summary = Summary.Parse(Assert.Single(log, line => line.Contains("The display ended.")));
        }

        // 11 frames from the first stream and 8 from the second were all decoded (frames submitted together are
        // decoded in order and only the newest picture of the batch is shown); none was dropped as stale, which is
        // what a restart that did not reach the session would have caused, and none had to be discarded.
        Assert.Equal(19, summary.Decoded);
        Assert.True(summary.Shown >= 2, "each stream shows at least its newest picture");
        Assert.Equal(0, summary.Dropped);
        Assert.Equal(0, summary.Discarded);
        Assert.Equal(0, summary.Refused);
    }
}
