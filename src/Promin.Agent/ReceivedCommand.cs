namespace Promin.Agent;

/// <summary>
/// <c>--received</c>: what the person sees after the console sends files. The helper runs as the signed-in person
/// so the files open in their session, and it tells them where the files are when none opened.
/// </summary>
internal static class ReceivedCommand
{
    /// <summary>
    /// Opens each path that is not dangerous and returns how many opened. When none did (all dangerous, all
    /// failed, or no path given), calls <paramref name="notify"/> with the count of items received and the folder.
    /// </summary>
    public static int Run(
        int count, string prominFolder, IReadOnlyList<string> paths,
        Func<string, bool> isDangerous, Func<string, bool> open, Action<int, string> notify)
    {
        var opened = 0;
        foreach (var path in paths)
        {
            if (!isDangerous(path) && open(path))
            {
                opened++;
            }
        }

        if (opened == 0)
        {
            notify(count, prominFolder);
        }

        return opened;
    }
}
