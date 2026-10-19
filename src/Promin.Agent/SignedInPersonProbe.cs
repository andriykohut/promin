using System.Security.Principal;

namespace Promin.Agent;

/// <summary>
/// One log line at start: where received files would go, as whom, and whether the helper starts as that person
/// rather than as SYSTEM. It is how a PC that receives files but never opens them is diagnosed.
/// </summary>
internal static class SignedInPersonProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The line to log; it never throws, a failure is said in the line. The name it reports is
    /// <paramref name="currentName"/> (by default, who this thread runs as) read inside
    /// <see cref="ISignedInPerson.RunAsAsync{T}"/>, so the line shows who file work really runs as.
    /// </summary>
    public static async Task<string> DescribeAsync(ISignedInPerson person, Func<string>? currentName = null)
    {
        string name;
        string placed;
        try
        {
            if (person.Folders() is not { } folders)
            {
                return "Nobody is signed in: received files cannot be placed.";
            }

            name = await person.RunAsAsync(() => Task.FromResult((currentName ?? CurrentName)())).ConfigureAwait(false);
            placed = $"Received files go to {Path.Combine(folders.Desktop, "Promin")}, written as {name}.";
        }
        catch (Exception failure)
        {
            return $"Received files cannot be placed: {failure.Message}";
        }

        try
        {
            var helper = await person.RunHelperAsync(["--helper-check"], Wait).ConfigureAwait(false) switch
            {
                ExitCodes.Success => $"The helper starts as {name}.",
                Program.HelperRunsAsSystem => "The helper started as SYSTEM: received files will not be opened.",
                null => $"The helper did not finish within {Wait.TotalSeconds:0} seconds.",
                var code => $"The helper ended with code {code}.",
            };
            return $"{placed} {helper}";
        }
        catch (Exception failure)
        {
            return $"{placed} The helper cannot be started: {failure.Message}";
        }
    }

    private static string CurrentName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }
}
