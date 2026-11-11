using Promin.Protocol.Messages;

namespace Promin.Master.Core.Control;

/// <summary>
/// Decides when the console PC's clipboard text goes to the agent PC: only while the view is focused, never text over
/// <see cref="ControlLimits.MaxClipboardChars"/>, and never text already sent or text that came from the agent PC.
/// </summary>
public sealed class ClipboardRelay
{
    private string? _lastSent;
    private string? _lastRemote;

    /// <summary>The console PC's clipboard text changed. Returns the text to send, or null; text copied while the view is not focused is offered by <see cref="OnViewFocused"/>.</summary>
    public string? OnLocalCopy(string? text, bool viewFocused) => viewFocused ? Offer(text) : null;

    /// <summary>The view got focus while the clipboard holds <paramref name="text"/>. Returns the text to send, or null.</summary>
    public string? OnViewFocused(string? text) => Offer(text);

    /// <summary>The agent PC's clipboard text arrived and was put on this PC's clipboard.</summary>
    public void OnRemoteText(string text) => _lastRemote = text;

    private string? Offer(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > ControlLimits.MaxClipboardChars || text == _lastSent || text == _lastRemote)
        {
            return null;
        }

        return _lastSent = text;
    }
}
