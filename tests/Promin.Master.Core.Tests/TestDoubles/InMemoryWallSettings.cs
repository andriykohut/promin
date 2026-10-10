using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class InMemoryWallSettings : IWallSettings
{
    public string? QualityLabel { get; set; }

    public string? SharpnessName { get; set; }
}
