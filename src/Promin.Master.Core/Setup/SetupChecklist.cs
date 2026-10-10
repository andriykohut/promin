using System.Globalization;
using Promin.Protocol;

namespace Promin.Master.Core.Setup;

/// <summary>One line of the "A PC is missing?" checklist: a bold lead, then the rest of the sentence.</summary>
public sealed record ChecklistItem(int Number, string Lead, string Rest)
{
    /// <summary>The number as the list shows it: "1.".</summary>
    public string Label => $"{Number}.";
}

/// <summary>
/// The "A PC is missing?" checklist (design doc 6.6). Built here, not in XAML, so that the port
/// comes from <see cref="ProminPorts"/> and the wording is pinned by a test.
/// </summary>
public static class SetupChecklist
{
    public static IReadOnlyList<ChecklistItem> Items { get; } =
    [
        new(1, Strings.Check_OnLead, Strings.Check_OnRest),
        new(2, Strings.Check_NetworkLead, Strings.Check_NetworkRest),
        new(
            3,
            Strings.Check_FirewallLead,
            Phrase.Format(Strings.Check_FirewallRest, ("port", ProminPorts.Quic.ToString(CultureInfo.InvariantCulture)))),
    ];
}
