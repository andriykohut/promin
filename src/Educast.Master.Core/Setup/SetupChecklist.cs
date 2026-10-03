using Educast.Protocol;

namespace Educast.Master.Core.Setup;

/// <summary>One line of the "A PC is missing?" checklist: a bold lead, then the rest of the sentence.</summary>
public sealed record ChecklistItem(int Number, string Lead, string Rest)
{
    /// <summary>The number as the list shows it: "1.".</summary>
    public string Label => $"{Number}.";
}

/// <summary>
/// The "A PC is missing?" checklist (design doc 6.6). Built here, not in XAML, so that the port
/// comes from <see cref="EducastPorts"/> and the wording is pinned by a test.
/// </summary>
public static class SetupChecklist
{
    public static IReadOnlyList<ChecklistItem> Items { get; } =
    [
        new(1, "Is it switched on", ", and is Educast running on it? It starts with Windows after install."),
        new(2, "Is it on the same network", " as this PC? Guest Wi-Fi and separate VLANs do not work."),
        new(3, "Is its firewall letting Educast through?", $" Allow UDP port {EducastPorts.Quic}. The setup guide has the steps."),
    ];
}
