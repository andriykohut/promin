using System.Diagnostics;
using System.Runtime.InteropServices;
using Promin.Master.Core.Control;
using Promin.Protocol.Messages;

namespace Promin.Master.Control;

/// <summary>
/// Takes the keys meant for the agent PC before Windows on this PC acts on them, through a low-level keyboard hook
/// held from <see cref="Start"/> to <see cref="Stop"/>. A key that <see cref="ControlKeys.GoesToRemote"/> sends goes
/// to the send delegate and is kept from this PC unless <see cref="ControlKeys.AlsoReachesThisPc"/>; its up goes to
/// the agent PC when the down did, and reaches this PC when this PC saw the down. <see cref="Stop"/> sends an up for
/// every key still down on the agent PC. Start and stop it on the UI
/// thread: Windows calls the hook through the message loop of the thread that set it, and drops a hook that is slow
/// to answer, so the send delegate must return at once. Which keys were pressed is never logged.
/// </summary>
internal sealed class KeyboardCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_ALTDOWN = 0x20;
    private const uint LLKHF_UP = 0x80;
    private const int VK_CONTROL = 0x11;

    private readonly Action<KeyInput> _send;
    private readonly HeldKeys _held = new();

    // The native side holds a function pointer to this, so it must live as long as the hook.
    private readonly HookProcedure _onKey;
    private nint _hook;

    public KeyboardCapture(Action<KeyInput> send)
    {
        _send = send;
        _onKey = OnKey;
    }

    private delegate nint HookProcedure(int code, nint wParam, nint lParam);

    /// <summary>Whether the control view is full screen, where every key goes to the agent PC.</summary>
    public bool FullScreen { get; set; }

    /// <summary>Sets the hook, unless it is set. When Windows refuses, keys stay on this PC and the reason is logged.</summary>
    public void Start()
    {
        if (_hook != 0)
        {
            return;
        }

        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _onKey, GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            Trace.TraceWarning("The keyboard hook could not be set (Win32 error {0}); keys stay on this PC.", Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Removes the hook, if set, and sends an up for each key still down on the agent PC.</summary>
    public void Stop()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        foreach (var up in _held.ReleaseAll())
        {
            _send(up);
        }
    }

    public void Dispose() => Stop();

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(0, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
        var virtualKey = (int)data.VirtualKey;
        var key = new KeyInput((ushort)data.VirtualKey, (ushort)data.ScanCode, (data.Flags & LLKHF_EXTENDED) != 0, (data.Flags & LLKHF_UP) == 0);
        bool toRemote, here;
        if (key.Down)
        {
            toRemote = ControlKeys.GoesToRemote(virtualKey, (data.Flags & LLKHF_ALTDOWN) != 0, GetAsyncKeyState(VK_CONTROL) < 0, FullScreen);
            here = !toRemote || ControlKeys.AlsoReachesThisPc(virtualKey, FullScreen);
            if (toRemote)
            {
                _held.Down(key);
            }
        }
        else
        {
            toRemote = _held.Up(key);

            // Windows records a key after its low-level hooks have answered, so this says whether this PC saw the
            // down. Following it, not the form, leaves no key down on this PC when the form changed in between.
            here = GetAsyncKeyState(virtualKey) < 0;
        }

        if (toRemote)
        {
            _send(key);
        }

        return here ? CallNextHookEx(0, code, wParam, lParam) : 1;
    }

    /// <summary>KBDLLHOOKSTRUCT, what a low-level keyboard hook's <c>lParam</c> points to.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookProcedure procedure, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
