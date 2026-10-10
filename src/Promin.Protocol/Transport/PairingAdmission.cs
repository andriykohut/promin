namespace Promin.Protocol.Transport;

/// <summary>Which certificates an agent's pairing window lets through TLS.</summary>
public enum PairingAdmission
{
    /// <summary>Any certificate that names a device and does not contradict a pin. For a PC with no teacher PC pinned yet.</summary>
    Anyone = 0,

    /// <summary>
    /// Only a certificate that is already pinned, which is how a teacher PC that was removed from the room pairs
    /// again. Any other certificate is refused during TLS and does not use the window up, so it has had no guess
    /// at the room code.
    /// </summary>
    PinnedPeersOnly = 1,
}
