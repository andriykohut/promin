namespace Promin.Protocol.Sending;

/// <summary>
/// A file being sent could not be read on the console, or no longer holds the number of bytes
/// it declared. <see cref="Exception.InnerException"/> says why.
/// </summary>
public sealed class SendSourceException(string path, Exception inner)
    : IOException($"{path} could not be read.", inner)
{
    /// <summary>The item's path within the send, as in <see cref="OutgoingItem.Path"/>.</summary>
    public string Path { get; } = path;
}
