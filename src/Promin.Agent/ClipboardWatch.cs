using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Promin.Protocol.Messages;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>Text copied on this PC, and a way to put text on its clipboard.</summary>
internal interface IClipboardWatch : IDisposable
{
    /// <summary>Raised on the watch's own thread with text copied on this PC. Never raised for text <see cref="Write"/> put there.</summary>
    event Action<string>? Copied;

    /// <summary>Puts <paramref name="text"/> on the clipboard, on the watch's thread; returns before that is done.</summary>
    void Write(string text);
}

/// <summary>
/// Watches the clipboard from a message-only window on a thread of its own, started by the constructor. Text that is
/// empty or longer than <see cref="ControlLimits.MaxClipboardChars"/> is not reported. The clipboard's text is never
/// logged. When the window cannot be made the reason is logged and the watch does nothing.
/// </summary>
internal sealed class ClipboardWatch : IClipboardWatch
{
    private const string ClassName = "Promin.Agent.Clipboard";
    private const int OpenRetries = 3;
    private static readonly TimeSpan RetryGap = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly Lock ClassGate = new();

    // The native side holds a function pointer to this, so it must never be collected.
    private static readonly WindowProcedure s_windowProcedure = OnWindowMessage;
    private static bool s_classRegistered;

    // The watch of this thread's window. Windows calls a window's procedure on the thread that made it, and only there.
    [ThreadStatic]
    private static ClipboardWatch? t_owner;

    private readonly Action<string> _log;
    private readonly ConcurrentQueue<string> _writes = new();
    private readonly Thread _thread;
    private volatile nint _window;
    private uint _threadId;

    // Owns the clipboard while it is open: EmptyClipboard leaves SetClipboardData failing for a null owner.
    private nint _owner;

    // Only the watch's thread touches this.
    private string? _lastWritten;

    public ClipboardWatch(Action<string> log)
    {
        _log = log;
        var up = new TaskCompletionSource<bool>();
        _thread = new Thread(() => Run(up)) { Name = "Promin clipboard", IsBackground = true };
        _thread.Start();
        if (!up.Task.Wait(Limit))
        {
            _log("The clipboard watch did not start within 5 seconds.");
        }
    }

    public event Action<string>? Copied;

    public void Write(string text)
    {
        if (_window == 0)
        {
            return;
        }

        _writes.Enqueue(text);
        PostMessageW(_window, WM_APP, 0, 0);
    }

    public void Dispose()
    {
        if (_window == 0)
        {
            return;
        }

        _window = 0;
        if (!PostThreadMessageW(_threadId, WM_QUIT, 0, 0) || !_thread.Join(Limit))
        {
            _log("The clipboard watch did not stop.");
        }
    }

    private void Run(TaskCompletionSource<bool> up)
    {
        t_owner = this;
        RegisterClassOnce();
        var window = CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandleW(null), 0);
        if (window == 0 || !AddClipboardFormatListener(window))
        {
            _log($"The clipboard watch could not start (Win32 error {Marshal.GetLastWin32Error()}).");
            if (window != 0)
            {
                DestroyWindow(window);
            }

            up.TrySetResult(false);
            return;
        }

        _threadId = GetCurrentThreadId();
        _owner = window;
        _window = window;
        up.TrySetResult(true);
        while (GetMessageW(out var received, 0, 0, 0) > 0)
        {
            DispatchMessageW(ref received);
        }

        RemoveClipboardFormatListener(window);
        DestroyWindow(window);
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
                ClassName = ClassName,
            };
            s_classRegistered = RegisterClassExW(ref windowClass) != 0;
        }
    }

    private static nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_CLIPBOARDUPDATE:
                t_owner?.OnClipboardUpdate();
                return 0;

            case WM_APP:
                t_owner?.WriteQueued();
                return 0;

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void OnClipboardUpdate()
    {
        var text = Read();
        if (string.IsNullOrEmpty(text) || text.Length > ControlLimits.MaxClipboardChars || text == _lastWritten)
        {
            return;
        }

        // Another text was copied since the one written, so copying the written one again is news.
        _lastWritten = null;
        Copied?.Invoke(text);
    }

    private void WriteQueued()
    {
        while (_writes.TryDequeue(out var text))
        {
            if (!OpenWithRetry())
            {
                _log("The clipboard could not be opened to write to it.");
                continue;
            }

            try
            {
                EmptyClipboard();
                var chars = (text + "\0").ToCharArray();
                var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)(chars.Length * 2));
                if (memory == 0)
                {
                    _log("The clipboard text could not be allocated.");
                    continue;
                }

                var locked = GlobalLock(memory);
                if (locked == 0)
                {
                    GlobalFree(memory);
                    _log($"The clipboard text could not be locked (Win32 error {Marshal.GetLastWin32Error()}).");
                    continue;
                }

                Marshal.Copy(chars, 0, locked, chars.Length);
                GlobalUnlock(memory);
                if (SetClipboardData(CF_UNICODETEXT, memory) == 0)
                {
                    GlobalFree(memory);
                    _log($"The clipboard refused the text (Win32 error {Marshal.GetLastWin32Error()}).");
                }
                else
                {
                    // The update message for this write is handled after this returns, so the text is known by then.
                    _lastWritten = text;
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    private string? Read()
    {
        if (!OpenWithRetry())
        {
            _log("The clipboard could not be opened to read it.");
            return null;
        }

        try
        {
            var data = GetClipboardData(CF_UNICODETEXT);
            if (data == 0)
            {
                return null;
            }

            var locked = GlobalLock(data);
            try
            {
                return locked == 0 ? null : Marshal.PtrToStringUni(locked);
            }
            finally
            {
                GlobalUnlock(data);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private bool OpenWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(_owner))
            {
                return true;
            }

            if (attempt == OpenRetries)
            {
                return false;
            }

            Thread.Sleep(RetryGap);
        }
    }
}
