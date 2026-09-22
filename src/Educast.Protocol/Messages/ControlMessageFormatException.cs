namespace Educast.Protocol.Messages;

/// <summary>
/// Thrown when a control message frame is definitively corrupt or malformed.
/// </summary>
public sealed class ControlMessageFormatException : Exception
{
    public ControlMessageFormatException(string message) : base(message) { }
    public ControlMessageFormatException(string message, Exception innerException) : base(message, innerException) { }
}
