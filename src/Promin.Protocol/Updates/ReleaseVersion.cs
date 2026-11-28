using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Promin.Protocol.Updates;

/// <summary>
/// A Promin release's version, as its tag and its build carry it: <c>0.4.0</c> or <c>0.4.0-beta.1</c>. It orders by
/// SemVer 2.0 precedence, so a pre-release is lower than its release. Build metadata (<c>+3b97a8b</c>) is dropped.
/// </summary>
public sealed record ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The dot-separated pre-release identifiers; empty for a release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>
    /// Reads <paramref name="text"/>, with or without a leading <c>v</c> and build metadata. False for anything that is
    /// not three numbers without leading zeros, optionally followed by a pre-release of non-empty
    /// <c>[0-9A-Za-z-]</c> identifiers, the numeric ones also without leading zeros.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var rest = text.StartsWith('v') ? text[1..] : text;
        var plus = rest.IndexOf('+');
        if (plus >= 0)
        {
            rest = rest[..plus];
        }

        var dash = rest.IndexOf('-');
        var core = dash >= 0 ? rest[..dash] : rest;
        string[] preRelease = [];
        if (dash >= 0)
        {
            preRelease = rest[(dash + 1)..].Split('.');
            if (!preRelease.All(IsIdentifier))
            {
                return false;
            }
        }

        var numbers = core.Split('.');
        if (numbers.Length != 3
            || !TryNumber(numbers[0], out var major)
            || !TryNumber(numbers[1], out var minor)
            || !TryNumber(numbers[2], out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>The version <paramref name="assembly"/> was built as, from its informational version.</summary>
    /// <exception cref="InvalidOperationException">The assembly has no informational version, or it does not read as a version.</exception>
    public static ReleaseVersion Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParse(text, out var version)
            ? version
            : throw new InvalidOperationException($"{assembly.GetName().Name} has no readable version: \"{text}\".");
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        // A release ranks above any of its pre-releases.
        if (PreRelease.Count == 0 || other.PreRelease.Count == 0)
        {
            return other.PreRelease.Count.CompareTo(PreRelease.Count);
        }

        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var order = CompareIdentifiers(PreRelease[i], other.PreRelease[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in PreRelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        PreRelease.Count == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{string.Join('.', PreRelease)}";

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return text.Length > 0
            && text.All(char.IsAsciiDigit)
            && (text.Length == 1 || text[0] != '0')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsIdentifier(string identifier) =>
        identifier.Length > 0
        && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && !(identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit));

    // Numeric identifiers compare as numbers and rank below alphanumeric ones, which compare in ASCII order.
    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        // Without leading zeros, the longer number is the larger one.
        return leftNumeric && left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right);
    }
}
