using System.Runtime.InteropServices;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>The notice that this PC is being controlled remotely.</summary>
internal interface IControlTab : IDisposable
{
    /// <summary>Puts the tab up. False when no window could be made, or it could not be kept out of screen capture.</summary>
    bool Show();

    /// <summary>Takes the tab down. Does nothing when it is not up.</summary>
    void Hide();
}

/// <summary>
/// A small tab hanging from the top centre of the main screen, in look A of the remote control spec. It never takes
/// focus or clicks, and it is left out of screen capture, so the console does not see it. Without that exclusion the
/// tab is not shown at all: a PC must not be controlled without it, and must not show it to the one controlling.
/// </summary>
/// <remarks>
/// The window belongs to a thread that lives from <see cref="Show"/> to <see cref="Hide"/>. Once a second it is raised
/// above other topmost windows, and a change of screens makes it move to the main screen of the new arrangement.
/// </remarks>
internal sealed class ControlTab(Action<string> log) : IControlTab
{
    private const string ClassName = "Promin.Agent.ControlTab";
    private const string Face = "Segoe UI";
    private const int SW_SHOWNOACTIVATE = 4;

    // As Windows writes colours: 0xBBGGRR. The edge is rgba(0,0,0,.18) over the surface, as an opaque colour.
    private const uint Surface = 0xFBFBFB;
    private const uint Edge = 0xCECECE;
    private const uint Dot = 0x0A42D4;
    private const uint Ink = 0x1A1A1A;

    // Sizes at 96 DPI, in pixels.
    private const int FontSize = 14;
    private const int PadX = 16;
    private const int PadY = 6;
    private const int DotSize = 7;
    private const int Gap = 8;
    private const int Radius = 8;

    // Posted by the window to the message loop, which moves it.
    private const uint ScreensChanged = WM_APP;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly Lock ClassGate = new();

    // The native side holds a function pointer to this, so it must never be collected.
    private static readonly WindowProcedure s_windowProcedure = OnWindowMessage;
    private static bool s_classRegistered;

    private Thread? _thread;
    private uint _threadId;

    public bool Show()
    {
        if (_thread is not null)
        {
            return true;
        }

        var up = new TaskCompletionSource<uint?>();
        var thread = new Thread(() => Run(up)) { Name = "Promin control tab", IsBackground = true };
        try
        {
            thread.Start();
        }
        catch (OutOfMemoryException e)
        {
            log($"The control tab's thread could not start: {e.Message}");
            return false;
        }

        // Whoever settles this first decides: a window that comes up after the limit takes itself down again.
        if (!up.Task.Wait(Limit) && up.TrySetResult(null))
        {
            log($"The control tab did not appear within {Limit.TotalSeconds:F0} seconds.");
        }

        if (up.Task.Result is not { } threadId)
        {
            return false;
        }

        (_thread, _threadId) = (thread, threadId);
        return true;
    }

    public void Hide()
    {
        if (_thread is not { } thread)
        {
            return;
        }

        _thread = null;
        if (!PostThreadMessageW(_threadId, WM_QUIT, 0, 0) || !thread.Join(Limit))
        {
            const string Why = "The control tab did not close, so the agent ends itself, which closes it.";
            log(Why);
            Environment.FailFast(Why);
        }
    }

    public void Dispose() => Hide();

    /// <param name="up">Settled with this thread's id once the tab is up, or null when it cannot be.</param>
    private void Run(TaskCompletionSource<uint?> up)
    {
        nint window;
        Rect bounds;
        try
        {
            RegisterClassOnce();
            (window, bounds) = Make();
        }
        catch (InvalidOperationException e)
        {
            if (!up.Task.IsCompleted)
            {
                log(e.Message);
            }

            up.TrySetResult(null);
            return;
        }

        if (up.TrySetResult(GetCurrentThreadId()))
        {
            SetTimer(0, 0, 1000, 0);
            while (GetMessageW(out var received, 0, 0, 0) > 0)
            {
                if (received.Id is WM_TIMER or ScreensChanged)
                {
                    try
                    {
                        (window, bounds) = received.Id == ScreensChanged || window == 0 ? Rebuild(window) : (window, bounds);
                    }
                    catch (InvalidOperationException e)
                    {
                        // Nothing to show on until the next second; the tab was taken down first.
                        window = 0;
                        log(e.Message);
                    }

                    if (window != 0)
                    {
                        SetWindowPos(window, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SWP_NOACTIVATE);
                    }
                }
                else
                {
                    DispatchMessageW(ref received);
                }
            }
        }

        DestroyWindow(window);
    }

    private static (nint Window, Rect Bounds) Rebuild(nint old)
    {
        DestroyWindow(old);
        return Make();
    }

    /// <summary>Makes the tab on the main screen and shows it, drawn.</summary>
    /// <exception cref="InvalidOperationException">The window could not be made, or kept out of capture.</exception>
    private static (nint Window, Rect Bounds) Make()
    {
        var monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            throw new InvalidOperationException("The control tab has no screen to go on.");
        }

        var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? (int)dpiX : 96;
        var text = MeasureText(dpi);
        var width = Scale(PadX * 2 + DotSize + Gap, dpi) + text.Width;
        var height = Scale(PadY * 2, dpi) + text.Height;
        var bounds = new Rect { Left = (info.Bounds.Left + info.Bounds.Right - width) / 2, Top = info.Bounds.Top };
        bounds.Right = bounds.Left + width;
        bounds.Bottom = bounds.Top + height;

        var window = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED, ClassName, null, WS_POPUP,
            bounds.Left, bounds.Top, width, height, 0, 0, GetModuleHandleW(null), 0);
        if (window == 0)
        {
            throw new InvalidOperationException($"The control tab could not be created (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        // Without a layered window, clicks do not pass through a window of another process.
        if (!SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(window);
            throw new InvalidOperationException($"The control tab could not be made click-through (Win32 error {error}).");
        }

        if (!SetWindowDisplayAffinity(window, WDA_EXCLUDEFROMCAPTURE))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyWindow(window);
            throw new InvalidOperationException($"The control tab could not be kept out of screen capture (Win32 error {error}).");
        }

        // The window's top corners are rounded above the window, so only the bottom ones show.
        var radius = Scale(Radius, dpi);
        SetWindowRgn(window, CreateRoundRectRgn(0, -radius, width + 1, height + 1, 2 * radius, 2 * radius), false);
        SetWindowLongPtr(window, GWLP_USERDATA, dpi);
        ShowWindow(window, SW_SHOWNOACTIVATE);
        UpdateWindow(window);
        return (window, bounds);
    }

    private static (int Width, int Height) MeasureText(int dpi)
    {
        var dc = GetDC(0);
        var font = CreateFont(dpi);
        var previous = SelectObject(dc, font);
        var box = new Rect();
        var height = DrawTextW(dc, Strings.ControlTab_Text, -1, ref box, DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
        SelectObject(dc, previous);
        DeleteObject(font);
        ReleaseDC(0, dc);
        return (box.Right, height);
    }

    private static nint CreateFont(int dpi) =>
        CreateFontW(-Scale(FontSize, dpi), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, Face);

    private static int Scale(int pixels, int dpi) => pixels * dpi / 96;

    private static void RegisterClassOnce()
    {
        lock (ClassGate)
        {
            if (s_classRegistered)
            {
                return;
            }

            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(s_windowProcedure),
                Instance = GetModuleHandleW(null),
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"The control tab class could not be registered (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            s_classRegistered = true;
        }
    }

    private static nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_PAINT:
                var dc = BeginPaint(hwnd, out var paint);
                GetClientRect(hwnd, out var client);
                Draw(dc, client, (int)GetWindowLongPtr(hwnd, GWLP_USERDATA));
                EndPaint(hwnd, ref paint);
                return 0;

            case WM_DISPLAYCHANGE:
                PostMessageW(hwnd, ScreensChanged, 0, 0);
                return 0;

            case WM_CLOSE:
                return 0;

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private static void Draw(nint dc, Rect client, int dpi)
    {
        var radius = Scale(Radius, dpi);
        var surface = CreateSolidBrush(Surface);
        var edge = CreatePen(0, 1, Edge);
        var oldBrush = SelectObject(dc, surface);
        var oldPen = SelectObject(dc, edge);
        RoundRect(dc, 0, -radius, client.Right, client.Bottom, 2 * radius, 2 * radius);

        var dot = CreateSolidBrush(Dot);
        SelectObject(dc, dot);
        SelectObject(dc, GetStockObject(NULL_PEN));
        var dotSize = Scale(DotSize, dpi);
        var left = Scale(PadX, dpi);
        var top = (client.Bottom - dotSize) / 2;
        Ellipse(dc, left, top, left + dotSize + 1, top + dotSize + 1);

        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, Ink);
        var font = CreateFont(dpi);
        var oldFont = SelectObject(dc, font);
        var text = client with { Left = left + dotSize + Scale(Gap, dpi) };
        DrawTextW(dc, Strings.ControlTab_Text, -1, ref text, DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        SelectObject(dc, oldFont);
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(font);
        DeleteObject(dot);
        DeleteObject(edge);
        DeleteObject(surface);
    }
}
