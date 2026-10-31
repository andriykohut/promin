using System.Runtime.InteropServices;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// The lock screen: one dark topmost window on every screen, in look A of the screen lock spec. The primary screen
/// carries the lock glyph, the message and the small line, the others the glyph alone. It asks Windows for nothing to
/// keep the screen on: a PC that sleeps comes back locked. Called by <see cref="ScreenLock"/>, one call at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> The windows belong to a thread that lives from <see cref="Show"/> to <see cref="Hide"/>, and
/// everything but those two and <see cref="Yield"/> runs on it.
/// </para>
/// <para>
/// <b>Staying in front.</b> Once a second the windows are raised, except the primary screen's while a broadcast picture
/// has it, and the screens are compared with the ones the windows cover. Windows reports a change of screens, and the
/// message only brings the comparison forward: a change it does not report is still caught within a second.
/// </para>
/// <para>
/// <b>Failing.</b> Nothing catches a failure once the windows are up. It ends the process, which takes the windows
/// and the input block with it, the service starts the agent again, and the agent locks the PC or logs why it cannot.
/// Windows gone and input still held is the one outcome that must not happen, so this thread never ends by itself.
/// </para>
/// </remarks>
internal sealed class LockScreenWindow(Action<string> log) : ILockScreen
{
    private const string ClassName = "Promin.Agent.Lock";
    private const string GlyphFace = "Segoe Fluent Icons";
    private const string MessageFace = "Segoe UI Semibold";
    private const string SmallLineFace = "Segoe UI";
    private const string LockGlyph = "\uE72E";

    // As Windows writes colours: 0xBBGGRR.
    private const uint Dark = 0x141110;
    private const uint Orange = 0x1F5AFF;
    private const uint White = 0xFFFFFF;
    private const uint Grey = 0xA6A4A4;

    private const uint OneLine = DT_CENTER | DT_SINGLELINE | DT_NOPREFIX;

    // DT_EDITCONTROL is what makes DT_WORDBREAK break a word too long for a line.
    private const uint Wrapped = DT_CENTER | DT_WORDBREAK | DT_EDITCONTROL | DT_NOPREFIX;

    // Posted by the windows to the message loop, which raises them.
    private const uint ScreensChanged = WM_APP;

    private static readonly TimeSpan ShowLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HideLimit = TimeSpan.FromSeconds(5);
    private static readonly Lock ClassGate = new();

    // The native side holds a function pointer to this, so it must never be collected.
    private static readonly WindowProcedure s_windowProcedure = OnWindowMessage;
    private static bool s_classRegistered;

    // The message on this thread's windows. Windows calls a window's procedure on the thread that made it, and only there.
    [ThreadStatic]
    private static string? t_message;

    private readonly record struct Screen(Rect Bounds, bool Primary);

    private readonly record struct Cover(nint Window, Screen Screen);

    private List<Cover> _covers = [];
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _yielded;

    /// <summary>
    /// Covers every screen, and returns once the windows are up and drawn. False when they were not within five
    /// seconds, with the reason logged; nothing stays on screen then, and nothing is thrown.
    /// </summary>
    public bool Show(string message)
    {
        _yielded = false;
        var up = new TaskCompletionSource<bool>();
        var thread = new Thread(() => Run(message, up)) { Name = "Promin lock screen", IsBackground = true };
        try
        {
            thread.Start();
        }
        catch (OutOfMemoryException e)
        {
            log($"The lock window's thread could not start: {e.Message}");
            return false;
        }

        // Whoever settles this first decides: windows that come up after the limit take themselves down again.
        if (!up.Task.Wait(ShowLimit) && up.TrySetResult(false))
        {
            log($"The lock window did not appear within {ShowLimit.TotalSeconds:F0} seconds.");
        }

        var shown = up.Task.Result;
        _thread = shown ? thread : null;
        return shown;
    }

    /// <summary>Takes the windows down. Does nothing when none are up. Throws nothing; windows that do not go in time are logged.</summary>
    public void Hide()
    {
        if (_thread is not { } thread)
        {
            return;
        }

        _thread = null;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        if (!thread.Join(HideLimit))
        {
            log($"The lock window did not close within {HideLimit.TotalSeconds:F0} seconds. It is left to finish by itself.");
        }
    }

    /// <summary>
    /// True stops raising the primary screen's window, so that a broadcast picture can sit above it. False takes that
    /// back, and the window is raised within a second.
    /// </summary>
    public void Yield(bool toPicture) => _yielded = toPicture;

    public void Dispose() => Hide();

    private void Run(string message, TaskCompletionSource<bool> up)
    {
        t_message = message;
        _threadId = GetCurrentThreadId();
        try
        {
            RegisterClassOnce();
            _covers = MakeCovers(Screens());
        }
        catch (InvalidOperationException e)
        {
            // Any windows already made go with this thread.
            log(e.Message);
            up.TrySetResult(false);
            return;
        }

        Raise();
        if (up.TrySetResult(true))
        {
            SetTimer(0, 0, 1000, 0);
            while (GetMessageW(out var received, 0, 0, 0) > 0)
            {
                if (received.Id is WM_TIMER or ScreensChanged)
                {
                    Raise();
                }
                else
                {
                    DispatchMessageW(ref received);
                }
            }
        }

        Destroy(_covers);
    }

    private void Raise()
    {
        var screens = Screens();

        // No screens at all is Windows between two arrangements, not a reason to take the windows down.
        // shortcut: windows made while a broadcast picture is up land above it until the broadcast ends; upgrade if
        // screens changing during a broadcast turns out to matter.
        if (screens.Count > 0 && !screens.SequenceEqual(_covers.Select(cover => cover.Screen)))
        {
            var old = _covers;
            _covers = MakeCovers(screens);
            Destroy(old);
        }

        foreach (var (window, screen) in _covers)
        {
            if (screen.Primary && _yielded)
            {
                continue;
            }

            var bounds = screen.Bounds;
            SetWindowPos(window, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SWP_NOACTIVATE);
            if (screen.Primary)
            {
                SetForegroundWindow(window); // allowed or not as Windows sees fit; refused while Ctrl+Alt+Del is up
            }
        }
    }

    private static List<Screen> Screens()
    {
        var screens = new List<Screen>();
        EnumDisplayMonitors(
            0, 0,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfoW(monitor, ref info))
                {
                    screens.Add(new Screen(info.Bounds, (info.Flags & MONITORINFOF_PRIMARY) != 0));
                }

                return true;
            },
            0);
        return screens;
    }

    /// <exception cref="InvalidOperationException">There is no screen, or a window could not be made.</exception>
    private static List<Cover> MakeCovers(List<Screen> screens)
    {
        if (screens.Count == 0)
        {
            throw new InvalidOperationException("The lock window has no screen to go on.");
        }

        var covers = new List<Cover>(screens.Count);
        foreach (var screen in screens)
        {
            var bounds = screen.Bounds;
            var window = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName, null, WS_POPUP,
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0, 0, GetModuleHandleW(null), 0);
            if (window == 0)
            {
                throw new InvalidOperationException($"The lock window could not be created (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            SetWindowLongPtr(window, GWLP_USERDATA, screen.Primary ? 1 : 0);
            ShowWindow(window, SW_SHOW);
            UpdateWindow(window); // Show returns with the text on screen, so input is never blocked before it is
            covers.Add(new Cover(window, screen));
        }

        return covers;
    }

    private static void Destroy(List<Cover> covers)
    {
        foreach (var cover in covers)
        {
            DestroyWindow(cover.Window);
        }
    }

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
                Background = CreateSolidBrush(Dark), // dark from the first instant, before anything is drawn
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"The lock window class could not be registered (Win32 error {Marshal.GetLastWin32Error()}).");
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
                Draw(dc, client.Right, client.Bottom, GetWindowLongPtr(hwnd, GWLP_USERDATA) != 0 ? t_message : null);
                EndPaint(hwnd, ref paint);
                return 0;

            case WM_DISPLAYCHANGE:
                // Sent to one window after another: the message loop looks at the screens once they have all heard.
                PostMessageW(hwnd, ScreensChanged, 0, 0);
                return 0;

            case WM_CLOSE:
                return 0; // Alt+F4, should it get past the block

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Draws the lock's content on <paramref name="dc"/>, a dark surface of <paramref name="width"/> by
    /// <paramref name="height"/> pixels, centred as one column. A null <paramref name="message"/> draws the glyph
    /// alone. An empty one puts the default text in the message's place, with no small line under it.
    /// </summary>
    private static void Draw(nint dc, int width, int height, string? message)
    {
        SetBkMode(dc, TRANSPARENT);
        var glyph = LockLayout.Share(width, LockLayout.GlyphShare);
        if (message is null)
        {
            Write(dc, LockGlyph, glyph, GlyphFace, FW_NORMAL, Orange, new Rect { Right = width, Bottom = height }, OneLine | DT_VCENTER);
            return;
        }

        var large = message.Length > 0 ? message : Strings.LockScreen_Locked;
        var margin = LockLayout.Share(width, LockLayout.SideMarginShare);
        var text = new Rect { Left = margin, Right = width - margin };
        var font = LockLayout.MessageFontHeight(
            width, height, size => Write(dc, large, size, MessageFace, FW_SEMIBOLD, White, text, Wrapped | DT_CALCRECT));
        var largeHeight = Write(dc, large, font, MessageFace, FW_SEMIBOLD, White, text, Wrapped | DT_CALCRECT);

        var leading = LockLayout.Share(font, LockLayout.SpareLeadingShare);

        var small = LockLayout.Share(width, LockLayout.SmallLineShare);
        var smallGap = LockLayout.Share(width, LockLayout.SmallLineGapShare);
        var line = new Rect { Right = width };
        var smallHeight = message.Length > 0
            ? smallGap + Write(dc, Strings.LockScreen_Locked, small, SmallLineFace, FW_NORMAL, Grey, line, OneLine | DT_CALCRECT)
            : 0;

        var gap = LockLayout.Share(width, LockLayout.GlyphGapShare);
        var top = (height - glyph - gap - (largeHeight - (2 * leading)) - smallHeight) / 2;
        Write(dc, LockGlyph, glyph, GlyphFace, FW_NORMAL, Orange, line with { Top = top, Bottom = top + glyph }, OneLine | DT_VCENTER);

        top += glyph + gap - leading;
        Write(dc, large, font, MessageFace, FW_SEMIBOLD, White, text with { Top = top, Bottom = top + largeHeight }, Wrapped);

        if (message.Length > 0)
        {
            top += largeHeight - leading + smallGap;
            Write(dc, Strings.LockScreen_Locked, small, SmallLineFace, FW_NORMAL, Grey, line with { Top = top, Bottom = height }, OneLine);
        }
    }

    /// <summary>Draws <paramref name="text"/>, or with <see cref="DT_CALCRECT"/> only measures it, and returns its height in pixels.</summary>
    /// <param name="size">The font's em height in pixels, as a browser takes a font size. That is why it goes in negative.</param>
    private static int Write(nint dc, string text, int size, string face, int weight, uint colour, Rect box, uint format)
    {
        var font = CreateFontW(-size, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, face);
        var previous = SelectObject(dc, font);
        SetTextColor(dc, colour);
        var height = DrawTextW(dc, text, -1, ref box, format);
        SelectObject(dc, previous);
        DeleteObject(font);
        return height;
    }
}
