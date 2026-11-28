namespace Promin.Protocol.Messages;

/// <summary>
/// How an agent PC answered an offered setup, carried by <see cref="UpdateAnswer"/>. The codec writes enums as numbers,
/// and these numbers are part of the fixed update path: they never change, and new outcomes take new numbers.
/// </summary>
public enum UpdateOutcome
{
    /// <summary>The setup is newer, its signature holds, and it is saved: the agent is about to exit for the service to run it.</summary>
    Accepted = 0,

    /// <summary>The project's signature does not hold for the setup and the version offered; nothing was kept.</summary>
    BadSignature = 1,

    /// <summary>The version offered is not newer than the one installed; nothing was kept.</summary>
    NotNewer = 2,

    /// <summary>The setup could not be saved on that PC; nothing was kept.</summary>
    Failed = 3,
}
