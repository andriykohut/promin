using System.Runtime.InteropServices;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// The block on the keyboard and mouse: low-level hooks that swallow everything that arrives, on a thread of their
/// own. Ctrl+Alt+Del stays with Windows, which no hook reaches. Called by <see cref="ScreenLock"/>, one call at a time.
/// </summary>
/// <remarks>
/// <para>
/// Windows answers a low-level hook through the message loop of the thread that installed it, and that thread alone
/// removes it, so <see cref="Hold"/> starts a thread that does both, and <see cref="Release"/> tells it to stop. The
/// loop does nothing else: Windows drops a hook that takes too long to answer, and says nothing when it does.
/// </para>
/// <para>
/// The hooks end with the process, so an agent that is killed gives the keyboard and mouse back at once. They never
/// look at what they swallow: which keys were pressed is neither read nor kept.
/// </para>
/// </remarks>
internal sealed class InputBlock(Action<string> log) : IInputBlock
{
    private static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(5);

    // The native side holds a function pointer to this, so it must never be collected.
    private static readonly HookProcedure s_swallow = Swallow;

    private Thread? _thread;
    private uint _threadId;

    /// <summary>
    /// Blocks keyboard and mouse, when they are not blocked already. When that fails, neither is blocked and the
    /// reason is logged. Throws nothing.
    /// </summary>
    public void Hold()
    {
        if (_thread is not null)
        {
            return;
        }

        var installed = new TaskCompletionSource<bool>();
        var thread = new Thread(() => Run(installed)) { Name = "Promin input block", IsBackground = true };
        try
        {
            thread.Start();
        }
        catch (OutOfMemoryException e)
        {
            log($"Keyboard and mouse could not be blocked: {e.Message}");
            return;
        }

        if (installed.Task.Result)
        {
            _thread = thread;
        }
    }

    /// <summary>Gives keyboard and mouse back. Does nothing when they are not blocked. Throws nothing; a block that does not end in time is logged.</summary>
    public void Release()
    {
        if (_thread is not { } thread)
        {
            return;
        }

        _thread = null;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        if (!thread.Join(ReleaseLimit))
        {
            log($"The block on keyboard and mouse did not end within {ReleaseLimit.TotalSeconds:F0} seconds. It is left to finish by itself.");
        }
    }

    public void Dispose() => Release();

    private void Run(TaskCompletionSource<bool> installed)
    {
        // Makes this thread's message queue: Release can come as soon as Hold returns, and cannot post to a thread without one.
        PeekMessageW(out _, 0, 0, 0, PM_NOREMOVE);
        _threadId = GetCurrentThreadId();

        var module = GetModuleHandleW(null);
        var keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, s_swallow, module, 0);
        var mouse = keyboard == 0 ? 0 : SetWindowsHookExW(WH_MOUSE_LL, s_swallow, module, 0);
        if (mouse != 0)
        {
            installed.SetResult(true);
            while (GetMessageW(out _, 0, 0, 0) > 0)
            {
            }
        }
        else
        {
            log($"Keyboard and mouse could not be blocked (Win32 error {Marshal.GetLastWin32Error()}).");
            installed.SetResult(false);
        }

        // Windows refuses, harmlessly, the one that was never made.
        UnhookWindowsHookEx(mouse);
        UnhookWindowsHookEx(keyboard);
    }

    private static nint Swallow(int code, nint wParam, nint lParam) =>
        code < 0 ? CallNextHookEx(0, code, wParam, lParam) : 1;
}
