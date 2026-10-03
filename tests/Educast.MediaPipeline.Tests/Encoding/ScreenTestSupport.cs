using System.Runtime.InteropServices;
using Educast.MediaPipeline.Capture;

namespace Educast.MediaPipeline.Tests.Encoding;

internal static class ScreenTestSupport
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    // DuplicateOutput1 (how the capture gets an SDR picture when Windows colour management would
    // otherwise give it FP16) needs a per-monitor-v2 DPI-aware process, and the test host is not one.
    // A real application declares this in its manifest; here it is declared before the first capture.
    public static void RequireDpiAwareness()
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2); // fails harmlessly if the process is already aware
        Assert.True(DpiAwareness.IsPerMonitorV2(), "the test process is not per-monitor-v2 DPI-aware, which DuplicateOutput1 requires");
    }
}
