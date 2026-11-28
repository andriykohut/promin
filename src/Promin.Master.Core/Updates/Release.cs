using Promin.Protocol.Updates;

namespace Promin.Master.Core.Updates;

/// <summary>A published Promin release: its version, and where its two setups and their <c>.sig</c> files are downloaded from.</summary>
public sealed record Release(ReleaseVersion Version, Uri AgentSetup, Uri AgentSignature, Uri ConsoleSetup, Uri ConsoleSignature);

/// <summary>Where the console learns of releases.</summary>
public interface IReleaseSource
{
    /// <summary>The newest release that is not a pre-release. Null when it cannot be reached or read; only cancellation throws.</summary>
    Task<Release?> LatestAsync(CancellationToken ct);

    /// <summary>The release of <paramref name="version"/>, pre-releases included. Null when it cannot be reached or read; only cancellation throws.</summary>
    Task<Release?> ByVersionAsync(ReleaseVersion version, CancellationToken ct);
}
