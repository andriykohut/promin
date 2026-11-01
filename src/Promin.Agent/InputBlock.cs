using System.Runtime.InteropServices;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// The block on the keyboard and mouse: low-level hooks on a thread of their own, which swallow every press and move
/// and let releases through. Ctrl+Alt+Del stays with Windows, which no hook reaches. Called by
/// <see cref="ScreenLock"/>, one call at a time.
/// </summary>
/// <remarks>
/// <para>
/// Windows answers a low-level hook through the message loop of the thread that installed it, and that thread alone
/// removes it, so <see cref="Hold"/> starts a thread that does both, and <see cref="Release"/> tells it to stop.
/// </para>
/// <para>
/// Windows drops a hook that takes too long to answer, and says nothing when it does. So the thread runs at the highest
/// priority, and every 30 seconds it replaces both hooks with fresh ones: a dropped hook costs at most that long.
/// </para>
/// <para>
/// The hooks end with the process, so an agent that is killed gives the keyboard and mouse back at once. They look only
/// at the kind of event: which keys were pressed is neither read nor kept.
/// </para>
/// </remarks>
internal sealed class InputBlock(Action<string> log) : IInputBlock
{
    private const uint RenewalMilliseconds = 30_000;

    private static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(5);

    // The native side holds a function pointer to this, so it must never be collected.
    private static readonly HookProcedure s_onInput = OnInput;

    private Thread? _thread;
    private uint _threadId;

    /// <summary>The two hooks that make up the block.</summary>
    internal readonly record struct Hooks(nint Keyboard, nint Mouse);

    /// <summary>
    /// Blocks keyboard and mouse, when they are not blocked already. False when that fails: neither is blocked then, and
    /// the reason is logged. Throws nothing.
    /// </summary>
    public bool Hold()
    {
        if (_thread is not null)
        {
            return true;
        }

        var installed = new TaskCompletionSource<bool>();
        var thread = new Thread(() => Run(installed))
        {
            Name = "Promin input block",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        try
        {
            thread.Start();
        }
        catch (OutOfMemoryException e)
        {
            log($"The keyboard and mouse hooks' thread could not start: {e.Message}");
            return false;
        }

        var held = installed.Task.Result;
        _thread = held ? thread : null;
        return held;
    }

    /// <summary>
    /// Gives keyboard and mouse back. Does nothing when they are not blocked. Throws nothing, and does not return when
    /// the block will not end: it logs that and ends the process, which takes the hooks with it, and the service starts
    /// the agent again.
    /// </summary>
    public void Release()
    {
        if (_thread is not { } thread)
        {
            return;
        }

        _thread = null;
        if (!PostThreadMessageW(_threadId, WM_QUIT, 0, 0) || !thread.Join(ReleaseLimit))
        {
            const string Why = "The block on keyboard and mouse did not end, so the agent ends itself, which ends the block.";
            log(Why);
            Environment.FailFast(Why);
        }
    }

    public void Dispose() => Release();

    /// <summary>
    /// Whether an input event goes on to Windows: a key or a button being released. Swallowed, a release would leave
    /// what was held at the moment the block started down for good: a game's W, a stuck Shift, a drag that never ends.
    /// </summary>
    /// <param name="inputEvent">The window message a low-level hook gets as its <c>wParam</c>, which names only the kind of event.</param>
    internal static bool LetsThrough(nint inputEvent) =>
        (uint)inputEvent is WM_KEYUP or WM_SYSKEYUP or WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP;

    /// <summary>
    /// Replaces <paramref name="current"/> with fresh hooks, or keeps it when they cannot be made. The fresh pair goes in
    /// before the old one comes out, so nothing gets through between the two.
    /// </summary>
    internal static Hooks Renew(Hooks current, Func<Hooks?> install, Action<Hooks> remove)
    {
        if (install() is not { } fresh)
        {
            return current;
        }

        remove(current);
        return fresh;
    }

    private void Run(TaskCompletionSource<bool> installed)
    {
        // Makes this thread's message queue: Release can come as soon as Hold returns, and cannot post to a thread without one.
        PeekMessageW(out _, 0, 0, 0, PM_NOREMOVE);
        _threadId = GetCurrentThreadId();

        if (Install() is not { } hooks)
        {
            installed.SetResult(false);
            return;
        }

        installed.SetResult(true);
        SetTimer(0, 0, RenewalMilliseconds, 0);
        while (GetMessageW(out var received, 0, 0, 0) > 0)
        {
            if (received.Id == WM_TIMER)
            {
                hooks = Renew(hooks, Install, Remove);
            }
        }

        Remove(hooks);
    }

    /// <summary>Installs a keyboard and a mouse hook, or neither: null when either cannot be made, with the reason logged.</summary>
    private Hooks? Install()
    {
        var module = GetModuleHandleW(null);
        var keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, s_onInput, module, 0);
        var mouse = keyboard == 0 ? 0 : SetWindowsHookExW(WH_MOUSE_LL, s_onInput, module, 0);
        if (mouse != 0)
        {
            return new Hooks(keyboard, mouse);
        }

        log($"A keyboard or mouse hook could not be installed (Win32 error {Marshal.GetLastWin32Error()}).");
        UnhookWindowsHookEx(keyboard); // refused, harmlessly, when it was never made
        return null;
    }

    // Removing a hook Windows has already dropped is refused, harmlessly.
    private static void Remove(Hooks hooks)
    {
        UnhookWindowsHookEx(hooks.Mouse);
        UnhookWindowsHookEx(hooks.Keyboard);
    }

    private static nint OnInput(int code, nint wParam, nint lParam) =>
        code >= 0 && !LetsThrough(wParam) ? 1 : CallNextHookEx(0, code, wParam, lParam);
}
