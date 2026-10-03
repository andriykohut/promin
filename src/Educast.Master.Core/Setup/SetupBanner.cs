namespace Educast.Master.Core.Setup;

public enum SetupBannerKind
{
    Warning,
    Success,
}

/// <summary>The one result bar the setup screen shows when there is a result to report.</summary>
public sealed record SetupBanner(SetupBannerKind Kind, string Title, string Message);
