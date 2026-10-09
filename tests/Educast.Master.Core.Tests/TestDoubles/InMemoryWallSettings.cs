using Educast.Master.Core.Wall;

namespace Educast.Master.Core.Tests.TestDoubles;

public sealed class InMemoryWallSettings : IWallSettings
{
    public string? QualityLabel { get; set; }

    public string? SharpnessName { get; set; }
}
