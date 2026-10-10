using Promin.Master.Core.Setup;

namespace Promin.Master.Core.Wall;

public enum WallMessageKind
{
    Error,
    Warning,
    Info,
}

/// <summary>The one bar above the wall.</summary>
public sealed record WallMessage(WallMessageKind Kind, string Title, string Text, string? ActionLabel = null)
{
    /// <summary>The picture source failed on air, or could not be started. Stays until the next start.</summary>
    public static WallMessage BroadcastStopped { get; } = new(
        WallMessageKind.Error,
        "The broadcast stopped.",
        "This PC could not capture or encode its screen. Start the broadcast again.");

    /// <summary>Paired PCs exist and none is on the network.</summary>
    public static WallMessage NoPcReachable { get; } = new(
        WallMessageKind.Error,
        "No student PCs can be reached.",
        "Check that they are switched on and on the same network as this PC.",
        "Set up devices");

    /// <summary>This PC has no hardware H.264 encoder.</summary>
    public static WallMessage SoftwareEncoding { get; } = new(
        WallMessageKind.Warning,
        "Quality reduced to 720p, 10 fps.",
        "This PC has no hardware video encoder, so Promin is encoding in software.");

    /// <summary>A PC that dropped off, or was not there at the start, joined the running broadcast. Shown for a few seconds.</summary>
    public static WallMessage IsBack(string name) => new(
        WallMessageKind.Info,
        $"{name} is back.",
        "It joined the broadcast by itself.");

    /// <summary>The room file exists but could not be read; the same words as the setup screen's.</summary>
    public static WallMessage RoomUnreadable { get; } = new(
        WallMessageKind.Error, SetupBanner.ReadError.Title, SetupBanner.ReadError.Message);

    /// <summary>The room could not be saved; the same words as the setup screen's. Clears on the next save that works.</summary>
    public static WallMessage RoomNotSaved { get; } = new(
        WallMessageKind.Error, SetupBanner.SaveError.Title, SetupBanner.SaveError.Message);
}
