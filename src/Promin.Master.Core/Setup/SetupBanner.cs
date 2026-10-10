using Promin.Protocol;

namespace Promin.Master.Core.Setup;

public enum SetupBannerKind
{
    Warning,
    Success,
    Error,
}

/// <summary>One bar on a page: a result to report, or something this PC could not do.</summary>
public sealed record SetupBanner(SetupBannerKind Kind, string Title, string Message)
{
    /// <summary>The room could not be written. Clears on the next save that works.</summary>
    public static SetupBanner SaveError { get; } = new(
        SetupBannerKind.Error,
        "Promin could not save this room.",
        "Check that this PC's disk is not full, then try again.");

    /// <summary>The room file exists but could not be read (locked, access denied).</summary>
    public static SetupBanner ReadError { get; } = new(
        SetupBannerKind.Error,
        "Promin could not read this room.",
        "Close any other copy of Promin, then start it again.");

    /// <summary>The discovery port is taken, so no student PC can be found from this screen.</summary>
    public static SetupBanner DiscoveryUnavailable { get; } = new(
        SetupBannerKind.Error,
        "Promin cannot look for student PCs.",
        $"Another program on this PC is using network port {ProminPorts.Discovery}. " +
        "Close any other copy of Promin, then open this screen again.");
}
