namespace Promin.Master.Core.Control;

public static class ControlKeys
{
    private const int Tab = 0x09;
    private const int Escape = 0x1B;
    private const int LeftWindows = 0x5B;
    private const int RightWindows = 0x5C;

    /// <summary>
    /// Whether a key goes to the agent PC rather than being handled here. Full screen sends everything; a window keeps
    /// Alt+Tab, Alt+Esc, Ctrl+Esc and the two Windows keys, so the console PC stays usable, and sends the rest.
    /// </summary>
    public static bool GoesToRemote(int virtualKey, bool altDown, bool ctrlDown, bool fullScreen) =>
        fullScreen || !(virtualKey is LeftWindows or RightWindows
            || (virtualKey == Tab && altDown)
            || (virtualKey == Escape && (altDown || ctrlDown)));
}
