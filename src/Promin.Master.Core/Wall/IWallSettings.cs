namespace Promin.Master.Core.Wall;

/// <summary>What the wall remembers between runs, besides the room itself.</summary>
public interface IWallSettings
{
    string? QualityLabel { get; set; }

    string? SharpnessName { get; set; }

    /// <summary>The language chosen in the console, as a culture name such as <c>uk</c>; null follows Windows.</summary>
    string? Language { get; set; }
}
