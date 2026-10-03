namespace Educast.Protocol;

/// <summary>
/// The product's network ports, both UDP. Nothing outside tests may carry a port literal; a
/// test that needs a port of its own passes it in (or asks the OS for one) rather than
/// using these, so that a test run never collides with a real Educast on the same machine.
/// </summary>
public static class EducastPorts
{
    /// <summary>The port an agent accepts QUIC connections on.</summary>
    public const int Quic = 17843;

    /// <summary>
    /// The port agents announce themselves to and the master console listens on, for LAN
    /// discovery.
    /// </summary>
    public const int Discovery = 17844;
}
