using Promin.MediaPipeline.Capture;
using static Promin.MediaPipeline.Tests.Hardware.GpuPictures;

namespace Promin.Agent.Tests.Hardware;

// This puts the real tab at the top centre of the main screen, on the PC of whoever is using it, for 5 s, and captures
// one frame of the screen while it is up. It moves no mouse and sends no keys. Run it by hand with the person looking
// (remove the Skip text, run, put it back), once on a 16:9 screen and once at 4:3. What it shows: a small light tab
// with a rounded bottom edge, an orange dot and "This PC is being controlled remotely", which the mouse passes through
// and which never takes focus. Compare it with docs/superpowers/specs/2026-11-11-remote-control-mockups/agent-notice.html,
// option A.
public class ControlTabTests(ITestOutputHelper output)
{
    private const string NeedsAScreen =
        "Shows a real tab for 5 s and captures the screen; run manually with the person looking.";

    [Fact(Skip = NeedsAScreen)]
    public async Task TheTab_ShowsFor5Seconds_AndIsAbsentFromACapturedFrame()
    {
        using var tab = new ControlTab(output.WriteLine);
        using var capture = new DxgiScreenCapture();

        Assert.True(tab.Show());
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); // painted before the capture
        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out var frame), "the capture gave no frame");
        var pixel = Read(capture.Device, frame.Texture, frame.Width, frame.Height);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        tab.Hide();

        // The tab's dot is #D4420A. It sits in the top strip around the centre, which the capture must not show it in.
        for (var y = 0; y < 60; y++)
        {
            for (var x = frame.Width / 2 - 300; x < frame.Width / 2 + 300; x++)
            {
                Assert.NotEqual(new Pixel(0x0A, 0x42, 0xD4), pixel(x, y));
            }
        }
    }
}
