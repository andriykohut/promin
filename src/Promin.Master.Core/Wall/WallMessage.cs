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
        Strings.Message_StoppedTitle,
        Strings.Message_StoppedText);

    /// <summary>Paired PCs exist and none is on the network.</summary>
    public static WallMessage NoPcReachable { get; } = new(
        WallMessageKind.Error,
        Strings.Message_NoPcTitle,
        Strings.Message_NoPcText,
        Strings.SetUpDevices);

    /// <summary>This PC has no hardware H.264 encoder.</summary>
    public static WallMessage SoftwareEncoding { get; } = new(
        WallMessageKind.Warning,
        Strings.Message_SoftwareTitle,
        Strings.Message_SoftwareText);

    /// <summary>A PC that dropped off, or was not there at the start, joined the running broadcast. Shown for a few seconds.</summary>
    public static WallMessage IsBack(string name) => new(
        WallMessageKind.Info,
        Phrase.Format(Strings.Message_BackTitle, ("name", name)),
        Strings.Message_BackText);

    /// <summary>The room file exists but could not be read; the same words as the setup screen's.</summary>
    public static WallMessage RoomUnreadable { get; } = new(
        WallMessageKind.Error, SetupBanner.ReadError.Title, SetupBanner.ReadError.Message);

    /// <summary>The room could not be saved; the same words as the setup screen's. Clears on the next save that works.</summary>
    public static WallMessage RoomNotSaved { get; } = new(
        WallMessageKind.Error, SetupBanner.SaveError.Title, SetupBanner.SaveError.Message);
}
