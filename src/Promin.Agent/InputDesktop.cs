using System.Runtime.InteropServices;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>Which desktop receives input right now. Injected input only reaches the user's desktop, called Default.</summary>
internal static class InputDesktop
{
    /// <summary>
    /// True when the input desktop is the user's. False on the lock screen or an admin prompt, which are other
    /// desktops, and when it cannot be told: Windows refuses to open those desktops to many callers.
    /// </summary>
    public static bool IsDefault()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            return GetUserObjectInformationW(desktop, UOI_NAME, buffer, 512, out _)
                && string.Equals(Marshal.PtrToStringUni(buffer), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseDesktop(desktop);
        }
    }
}
