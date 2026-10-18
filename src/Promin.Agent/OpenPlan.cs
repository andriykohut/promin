namespace Promin.Agent;

/// <summary>Which received items the helper is asked to open, by the spec's rule.</summary>
internal static class OpenPlan
{
    /// <summary>
    /// The full paths to open: nothing when <paramref name="open"/> is false; the folder when it is the only item
    /// sent; otherwise every file sent at the top level. Programs stay in the list, since the helper is what skips them.
    /// </summary>
    public static IReadOnlyList<string> For(IReadOnlyList<(string Name, bool IsFolder)> topLevel, bool open, string promin)
    {
        if (!open)
        {
            return [];
        }

        if (topLevel is [(var folder, true)])
        {
            return [Path.Combine(promin, folder)];
        }

        return [.. topLevel.Where(item => !item.IsFolder).Select(item => Path.Combine(promin, item.Name))];
    }
}
