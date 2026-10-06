namespace Educast.Agent;

/// <summary>What the process exits with. Anything but <see cref="Success"/> is also said in the log or on the console.</summary>
internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Something nobody planned for went wrong; the log has the exception.</summary>
    public const int Unexpected = 1;

    /// <summary>The command line, or the room code given on it, is not valid.</summary>
    public const int BadArguments = 2;

    /// <summary>QUIC is not available: this is not Windows 11.</summary>
    public const int QuicUnavailable = 3;

    /// <summary>The listener could not start: the port could not be bound, or this PC's own certificate cannot complete a handshake.</summary>
    public const int ListenerFailed = 4;

    /// <summary>A file the agent needs could not be read or written (identity, pinned teacher PCs, room code).</summary>
    public const int FileUnusable = 5;
}
