using Promin.Master.Core.Updates;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class FakeReleaseSource : IReleaseSource
{
    public Release? Latest { get; set; }

    public Dictionary<ReleaseVersion, Release> ByVersion { get; } = [];

    public List<ReleaseVersion> AskedFor { get; } = [];

    public Task<Release?> LatestAsync(CancellationToken ct) => Task.FromResult(Latest);

    public Task<Release?> ByVersionAsync(ReleaseVersion version, CancellationToken ct)
    {
        AskedFor.Add(version);
        return Task.FromResult(ByVersion.GetValueOrDefault(version));
    }
}

public sealed class FakeUpdateDownloads : IUpdateDownloads
{
    /// <summary>What <see cref="GetAsync"/> hands out: the set, or a download still under way.</summary>
    public Func<Release, bool, Task<UpdateSet?>> Get { get; set; } = (release, withConsole) => Task.FromResult<UpdateSet?>(
        new UpdateSet(
            new UpdateFile("agent.exe", release.Version, [1]),
            withConsole ? new UpdateFile("console.exe", release.Version, [2]) : null));

    public List<(Release Release, bool WithConsole)> Gets { get; } = [];

    public Dictionary<ReleaseVersion, UpdateFile> Cached { get; } = [];

    public Task<UpdateSet?> GetAsync(Release release, bool withConsole, CancellationToken ct)
    {
        Gets.Add((release, withConsole));
        return Get(release, withConsole);
    }

    public UpdateFile? CachedAgent(ReleaseVersion version) => Cached.GetValueOrDefault(version);
}

public sealed class FakeSelfUpdate : ISelfUpdate
{
    public bool Answer { get; set; } = true;

    public List<string> Started { get; } = [];

    public bool Start(string consoleSetupPath)
    {
        Started.Add(consoleSetupPath);
        return Answer;
    }
}
