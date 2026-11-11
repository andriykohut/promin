namespace Promin.Protocol.Messages;

public static class ControlLimits
{
    /// <summary>
    /// The most characters of clipboard text sent in one <see cref="ClipboardText"/>. Even when every character
    /// becomes a 6-byte JSON escape the message stays within <c>ControlMessageCodec.MaxFrameSize</c>.
    /// </summary>
    public const int MaxClipboardChars = 10_000;
}
