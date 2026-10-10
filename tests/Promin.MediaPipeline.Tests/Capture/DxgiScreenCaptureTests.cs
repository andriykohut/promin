using System.Runtime.InteropServices;
using Promin.MediaPipeline.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit.Abstractions;

namespace Promin.MediaPipeline.Tests.Capture;

// These tests need an interactive, unlocked desktop session with a GPU driver that supports
// Desktop Duplication, and they MOVE THE MOUSE POINTER. They cannot run on a headless CI agent;
// run them by hand on the target machine (remove the Skip text, run, put it back).
public class DxgiScreenCaptureTests(ITestOutputHelper output)
{
    private const string NeedsDesktop = "Requires an interactive desktop session; moves the pointer; run manually on target hardware.";

    [Fact(Skip = NeedsDesktop)]
    public void TryGetNextFrame_ReturnsAFrame_WithinFiveSeconds()
    {
        using var capture = StartCapture();

        var gotFrame = capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out var frame);

        Assert.True(gotFrame);
        Assert.True(frame.Width > 0);
        Assert.True(frame.Height > 0);
    }

    [Fact(Skip = NeedsDesktop)]
    public void PointerOnlyUpdate_StillYieldsAFrame()
    {
        using var capture = StartCapture();
        using var pointer = new PointerRestorer();

        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out _));
        Drain(capture); // let any other pending screen change go by, so what follows is the pointer's doing

        pointer.MoveTo(pointer.Start.X + (pointer.Start.X + 40 < Native.ScreenWidth ? 40 : -40), pointer.Start.Y);

        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(2), out _));
    }

    // Requirement 2 at its hardest: a presenter pointing at a still slide, with the capture just started.
    // PointerOnlyUpdate_StillYieldsAFrame cannot show this, because on a screen that keeps changing a
    // frame arrives for reasons that have nothing to do with the pointer. So this test first insists
    // that the screen really is still (and fails, saying so, if it never becomes still within 15 s:
    // run it on an idle desktop and leave the mouse and keyboard alone).
    //
    // It then starts a NEW capture and answers two separate questions, both written to the test output:
    //   1. Does a fresh duplication on a still screen ever deliver a real first image (LastPresentTime
    //      != 0), and after how long? Nothing moves during this phase.
    //   2. Once the pointer moves, does a frame arrive? This is the assertion.
    // If phase 1 produced no frame at all, phase 2 is exactly "pointer move, no desktop image yet".
    [Fact(Skip = NeedsDesktop)]
    public void StillScreen_FreshCapture_PointerMove_StillYieldsAFrame()
    {
        using (var probe = StartCapture())
        {
            output.WriteLine("-- probe capture: waiting for the screen to hold still");
            Assert.True(WaitForStillScreen(probe), "The screen never held still for 1.5 s within 15 s: this test needs an idle desktop.");
        }

        output.WriteLine("-- fresh capture, phase 1: nothing moves");
        using var capture = StartCapture();
        using var pointer = new PointerRestorer();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var framesInPhase1 = 0;
        TimeSpan? firstFrameAfter = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (capture.TryGetNextFrame(TimeSpan.FromMilliseconds(250), out _))
            {
                framesInPhase1++;
                firstFrameAfter ??= clock.Elapsed;
            }
        }
        output.WriteLine(capture.FirstRealUpdateAfter is { } real
            ? $"phase 1: first REAL screen update (LastPresentTime != 0) arrived {real.TotalMilliseconds:F0} ms after the duplication was made"
            : "phase 1: NO real screen update arrived within 3 s of the duplication being made");
        output.WriteLine($"phase 1: {framesInPhase1} frame(s) returned; first after {(firstFrameAfter is { } ff ? $"{ff.TotalMilliseconds:F0} ms" : "never")}");

        output.WriteLine("-- phase 2: moving the pointer");
        pointer.MoveTo(pointer.Start.X + (pointer.Start.X + 40 < Native.ScreenWidth ? 40 : -40), pointer.Start.Y);
        var moved = System.Diagnostics.Stopwatch.StartNew();
        var gotFrame = capture.TryGetNextFrame(TimeSpan.FromSeconds(2), out var frame);
        output.WriteLine($"phase 2: pointer-move frame {(gotFrame ? $"arrived after {moved.Elapsed.TotalMilliseconds:F0} ms" : "did NOT arrive within 2 s")}");

        Assert.True(gotFrame);
        Assert.True(frame.Width > 0);
    }
    [Fact(Skip = NeedsDesktop)]
    public void PointerIsDrawnIntoTheFrame()
    {
        using var capture = StartCapture();
        using var pointer = new PointerRestorer();

        pointer.MoveTo(200, 200);
        var withPointer = CaptureRegionAfterSettling(capture, 200, 200, 32);

        // The top-right corner: well away from the region, and nothing there reacts to the pointer.
        pointer.MoveTo(Native.ScreenWidth - 1, 0);
        var withoutPointer = CaptureRegionAfterSettling(capture, 200, 200, 32);

        Assert.NotEqual(withoutPointer, withPointer);
    }

    [Fact(Skip = NeedsDesktop)]
    public void FrameSize_MatchesThePrimaryDisplay()
    {
        Native.RequireDpiAwareness();
        using var capture = StartCapture();

        Assert.True(capture.TryGetNextFrame(TimeSpan.FromSeconds(5), out var frame));

        Assert.Equal(Native.ScreenWidth, frame.Width);
        Assert.Equal(Native.ScreenHeight, frame.Height);
    }

    // Takes frames for a short while after the pointer moved and returns the region of the last one.
    // Every frame carries the pointer as of the moment it was acquired, so the last one reflects the
    // move. The time bound matters: on a screen that never goes quiet (video, a game, a console
    // scrolling) "wait until nothing changes" would never finish.
    private static byte[] CaptureRegionAfterSettling(DxgiScreenCapture capture, int x, int y, int size)
    {
        Thread.Sleep(150); // let the compositor report the pointer move
        CapturedFrame last = default;
        var got = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMilliseconds(600) && capture.TryGetNextFrame(TimeSpan.FromMilliseconds(300), out var frame))
        {
            last = frame;
            got = true;
        }
        Assert.True(got, "no frame arrived after the pointer moved");
        return ReadRegion(capture.Device, last.Texture, x, y, size);
    }

    // DuplicateOutput1 (how the capture gets an SDR picture when Windows colour management would
    // otherwise give it FP16) needs a per-monitor-v2 DPI-aware process, and the test host is not one.
    // A real application declares this in its manifest; here we declare it before the first capture.
    //
    // The capture's first acquires on each new duplication are written to the test output (text only:
    // which timestamps were zero, accumulated frames, surface format, which call made the duplication).
    private DxgiScreenCapture StartCapture()
    {
        Native.RequireDpiAwareness();
        var capture = new DxgiScreenCapture();
        capture.Trace = output.WriteLine;
        return capture;
    }

    // True once a 1.5 s wait for the next frame comes back empty, i.e. nothing on screen changed.
    private static bool WaitForStillScreen(DxgiScreenCapture capture)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            Drain(capture);
            if (!capture.TryGetNextFrame(TimeSpan.FromMilliseconds(1500), out _))
                return true;
        }
        return false;
    }

    private static void Drain(DxgiScreenCapture capture)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMilliseconds(600) && capture.TryGetNextFrame(TimeSpan.FromMilliseconds(300), out _))
        {
        }
    }

    private static byte[] ReadRegion(ID3D11Device device, ID3D11Texture2D texture, int x, int y, int size)
    {
        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size,
            Height = (uint)size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        var context = device.ImmediateContext;
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0,
            new Vortice.Mathematics.Box(x, y, 0, x + size, y + size, 1));

        var pixels = new byte[size * size * 4];
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var row = 0; row < size; row++)
                Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, pixels, row * size * 4, size * 4);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
        return pixels;
    }

    // Moves the pointer for the test and puts it back where the person had it.
    private sealed class PointerRestorer : IDisposable
    {
        public (int X, int Y) Start { get; }

        public PointerRestorer()
        {
            Native.RequireDpiAwareness();
            Assert.True(Native.GetCursorPos(out var p));
            Start = (p.X, p.Y);
        }

        public void MoveTo(int x, int y) => Assert.True(Native.SetCursorPos(x, y));

        public void Dispose() => Native.SetCursorPos(Start.X, Start.Y);
    }

    private static class Native
    {
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private static readonly IntPtr PerMonitorAwareV2 = new(-4);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool IsProcessDPIAware();

        public static int ScreenWidth => GetSystemMetrics(SM_CXSCREEN);
        public static int ScreenHeight => GetSystemMetrics(SM_CYSCREEN);

        // A DPI-unaware process sees (and sets the pointer in) scaled coordinates, not the physical
        // pixels Desktop Duplication works in. The test host is unaware by default.
        public static void RequireDpiAwareness()
        {
            SetProcessDpiAwarenessContext(PerMonitorAwareV2); // fails harmlessly if the process is already aware
            Assert.True(IsProcessDPIAware(), "the test process is not DPI-aware, so screen coordinates would be scaled");
            Assert.True(DpiAwareness.IsPerMonitorV2(), "the test process is not per-monitor-v2 DPI-aware, which DuplicateOutput1 requires");
        }
    }
}
