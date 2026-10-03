namespace Educast.Protocol.Transport;

/// <summary>
/// The peer closed the control stream cleanly before a whole message arrived — either between
/// messages or part-way through one.
/// </summary>
/// <remarks>
/// A dedicated type rather than <see cref="InvalidOperationException"/> on purpose: callers need
/// to tell "the peer went away", which is routine, from "this process used a channel it had
/// already disposed", which is a bug. <see cref="ObjectDisposedException"/> derives from
/// <see cref="InvalidOperationException"/>, so catching that to handle a closed stream silently
/// swallowed the bug as well.
/// </remarks>
public sealed class ControlChannelClosedException : IOException
{
    public ControlChannelClosedException(string message) : base(message) { }
    public ControlChannelClosedException(string message, Exception innerException) : base(message, innerException) { }
}
