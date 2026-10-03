namespace Educast.Protocol.Transport;

/// <summary>
/// This machine's own certificate could not be used to complete a TLS handshake, so nothing
/// could ever connect to it. Thrown by <see cref="QuicAgentListener.StartAsync"/>.
/// </summary>
/// <remarks>
/// The TLS stack does not report this where one would look for it. A credential that will not
/// load — a certificate with no private key, or a key the platform cannot use — does not fail
/// when the listener starts; it fails later, once per inbound connection, as an
/// <c>AuthenticationException</c> out of the accept loop, which is exactly what a refused
/// stranger looks like. Left there, a broken agent would sit "rejecting" every master for ever
/// with nothing to say why. The listener therefore proves its credential with a handshake
/// against itself before it reports that it has started, and this is what it throws when that
/// handshake cannot be completed. <see cref="Exception.InnerException"/> carries what the TLS
/// stack said.
/// </remarks>
public sealed class LocalCredentialException : Exception
{
    public LocalCredentialException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
