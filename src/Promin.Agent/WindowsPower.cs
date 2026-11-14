using System.Runtime.InteropServices;
using Promin.Protocol.Messages;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// Shuts this PC down or restarts it at once, forcing every app closed in every session: unsaved work is lost and an
/// app that hangs does not hold it up. A shut down installs the updates Windows has waiting; a restart does not start
/// installing them. Never a hybrid (Fast Startup) shut down. The agent runs as SYSTEM, whose token holds the shutdown
/// privilege; it is switched on here before the call. Needs a real PC, so it is proven by the hand tests.
/// </summary>
internal static class WindowsPower
{
    /// <summary>True when Windows took the order. On a failure, logs the Win32 error code and returns false; never throws.</summary>
    public static bool Request(PowerAction action, Action<string> log)
    {
        if (!EnableShutdownPrivilege(out var privilegeError))
        {
            log($"This PC could not shut down or restart: the shutdown privilege could not be switched on (error {privilegeError}).");
            return false;
        }

        var flags = SHUTDOWN_FORCE_OTHERS | SHUTDOWN_FORCE_SELF
            | (action == PowerAction.Restart ? SHUTDOWN_RESTART : SHUTDOWN_POWEROFF | SHUTDOWN_INSTALL_UPDATES);
        var result = InitiateShutdownW(null, null, 0, flags, SHTDN_REASON_OTHER_PLANNED);
        if (result != 0)
        {
            log($"Windows refused to {(action == PowerAction.Restart ? "restart" : "shut down")} this PC (error {result}).");
            return false;
        }

        return true;
    }

    private static bool EnableShutdownPrivilege(out int error)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_FOR_PRIVILEGE, out var token))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, SE_SHUTDOWN_NAME, out var luid))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            var privilege = new TokenPrivilege { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            var adjusted = AdjustTokenPrivileges(token, false, ref privilege, 0, nint.Zero, nint.Zero);

            // AdjustTokenPrivileges reports success even when the token lacks the privilege; the last error tells.
            error = Marshal.GetLastWin32Error();
            return adjusted && error != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
