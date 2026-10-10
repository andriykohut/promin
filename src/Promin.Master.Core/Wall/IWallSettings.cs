namespace Promin.Master.Core.Wall;

/// <summary>What the wall remembers between runs, besides the room itself.</summary>
public interface IWallSettings
{
    string? QualityLabel { get; set; }

    string? SharpnessName { get; set; }
}
