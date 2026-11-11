namespace Promin.Master.Core.Wall;

/// <summary>What the wall remembers between runs, besides the room itself.</summary>
public interface IWallSettings
{
    string? QualityLabel { get; set; }

    string? SharpnessName { get; set; }

    /// <summary>The language chosen in the console, as a culture name such as <c>uk</c>; null follows Windows.</summary>
    string? Language { get; set; }

    /// <summary>
    /// Where the control window last stood in its window form: <c>x,y,width,height</c> in physical pixels. Null opens
    /// it beside the console's main window.
    /// </summary>
    string? ControlWindowPlacement { get; set; }
}
