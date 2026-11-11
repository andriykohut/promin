namespace Promin.Protocol.Transport;

/// <summary>
/// The first byte of every stream the agent PC sends the console PC on an ordinary connection, which says what the
/// rest of the stream is.
/// </summary>
public static class AgentStream
{
    /// <summary>A screen picture (JPEG); an empty payload means the screen is not available.</summary>
    public const byte Picture = 1;

    /// <summary>One frame of the control video.</summary>
    public const byte ControlVideo = 2;

    /// <summary>Returns a new array: <paramref name="kind"/> followed by <paramref name="payload"/>.</summary>
    public static byte[] Tag(byte kind, ReadOnlySpan<byte> payload)
    {
        var tagged = new byte[payload.Length + 1];
        tagged[0] = kind;
        payload.CopyTo(tagged.AsSpan(1));
        return tagged;
    }
}
