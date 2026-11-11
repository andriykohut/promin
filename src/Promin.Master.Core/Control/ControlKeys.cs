namespace Promin.Master.Core.Control;

public static class ControlKeys
{
    private const int Tab = 0x09;
    private const int Escape = 0x1B;
    private const int LeftWindows = 0x5B;
    private const int RightWindows = 0x5C;
    private const int LeftControl = 0xA2;
    private const int RightControl = 0xA3;
    private const int LeftAlt = 0xA4;
    private const int RightAlt = 0xA5;

    /// <summary>
    /// Whether a key goes to the agent PC rather than being handled here. Full screen sends everything; a window keeps
    /// Alt+Tab, Alt+Esc, Ctrl+Esc and the two Windows keys, so the console PC stays usable, and sends the rest.
    /// </summary>
    public static bool GoesToRemote(int virtualKey, bool altDown, bool ctrlDown, bool fullScreen) =>
        fullScreen || !(virtualKey is LeftWindows or RightWindows
            || (virtualKey == Tab && altDown)
            || (virtualKey == Escape && (altDown || ctrlDown)));

    /// <summary>
    /// Whether a key that goes to the agent PC also reaches this PC. In a window Ctrl and Alt do, so that Windows here
    /// sees them held when Tab or Esc completes one of the combinations a window keeps; a key kept from Windows is
    /// one it never saw pressed. <paramref name="virtualKey"/> is the left- or right-hand code a low-level keyboard
    /// hook reports.
    /// </summary>
    public static bool AlsoReachesThisPc(int virtualKey, bool fullScreen) =>
        !fullScreen && virtualKey is LeftControl or RightControl or LeftAlt or RightAlt;
}
