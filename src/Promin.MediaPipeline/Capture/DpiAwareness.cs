using System.Runtime.InteropServices;

namespace Promin.MediaPipeline.Capture;

/// <summary>
/// Reads, never changes, the process's DPI awareness. <c>IDXGIOutput5.DuplicateOutput1</c> needs a
/// per-monitor-v2 aware process, and that can only be declared by the application itself (its
/// manifest, or the host framework's setting) before it creates windows. A library that changed it
/// at run time would alter the whole application's scaling behind its back.
/// </summary>
internal static class DpiAwareness
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    public static bool IsPerMonitorV2()
    {
        try
        {
            return AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            return false; // Windows older than 10 1607
        }
    }
}
