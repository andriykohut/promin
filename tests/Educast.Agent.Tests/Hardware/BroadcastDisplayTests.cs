using Educast.MediaPipeline.Capture;
using Educast.MediaPipeline.Encoding;
using Educast.Protocol.Messages;
using Educast.Protocol.Video;
using Xunit.Abstractions;
using static Educast.Agent.Tests.Hardware.GpuPictures;

namespace Educast.Agent.Tests.Hardware;

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

    [Fact(Skip = NeedsWindow)]
    public async Task AWindowedDisplay_ShowsRealEncodedPictures_AndEndsCleanly()
    {
        var payloads = EncodedBroadcast(1024, 768, colours: 4);

        var log = await ShowAsync(payloads, output.WriteLine);

        Assert.Contains(log, line => line.Contains("The first picture is on screen"));
        var summary = Assert.Single(log, line => line.Contains("The display ended."));
        Assert.Contains($"Pictures shown: {payloads.Count},", summary);
        Assert.Contains("refused by the decoder: 0", summary);
        Assert.DoesNotContain(log, line => line.Contains("failed"));
    }

    /// <summary>Teardown while frames are still queued: it must not hang and must not throw.</summary>
    [Fact(Skip = NeedsWindow)]
    public async Task ADisplayRemovedWhileFramesAreStillArriving_EndsWithoutHangingOrThrowing()
    {
        var payloads = EncodedBroadcast(1024, 768, colours: 2);
        var display = await BroadcastDisplay.StartAsync(new StartBroadcast(1024, 768, 15), windowed: true, output.WriteLine);
        Assert.NotNull(display);

        var feeding = Task.Run(async () =>
        {
            foreach (var payload in payloads)
            {
                await display.SubmitFrameAsync(payload, CancellationToken.None); // never faults, whatever the display is doing
            }
        });
        await Task.Delay(150);

        var stopped = Task.Run(display.Dispose);

        await stopped.WaitAsync(TimeSpan.FromSeconds(15));
        await feeding.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(display.IsOpen);
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

        string summary;
        lock (log)
        {
            summary = Assert.Single(log, line => line.Contains("The display ended."));
        }

        // 11 pictures from the first stream and 8 from the second; none dropped as stale.
        Assert.Contains("Pictures shown: 19,", summary);
        Assert.Contains("frames dropped: 0,", summary);
    }
}
