using System.Globalization;
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
        Strings.Banner_SaveErrorTitle,
        Strings.Banner_SaveErrorText);

    /// <summary>The room file exists but could not be read (locked, access denied).</summary>
    public static SetupBanner ReadError { get; } = new(
        SetupBannerKind.Error,
        Strings.Banner_ReadErrorTitle,
        Strings.Banner_ReadErrorText);

    /// <summary>The discovery port is taken, so no agent PC can be found from this screen.</summary>
    public static SetupBanner DiscoveryUnavailable { get; } = new(
        SetupBannerKind.Error,
        Strings.Banner_DiscoveryTitle,
        Phrase.Format(Strings.Banner_DiscoveryText, ("port", ProminPorts.Discovery.ToString(CultureInfo.InvariantCulture))));
}
