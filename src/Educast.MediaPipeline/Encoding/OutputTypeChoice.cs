namespace Educast.MediaPipeline.Encoding;

internal static class OutputTypeChoice
{
    /// <summary>
    /// Which of the output types a transform offers after a format change to take: the first with the profile
    /// the encoder was configured with (so a baseline encoder stays baseline), else the first offered.
    /// -1 when nothing is offered. A type that does not state its profile (null) does not match.
    /// </summary>
    internal static int Pick(IReadOnlyList<uint?> profiles, uint? wantedProfile)
    {
        if (profiles.Count == 0)
            return -1;
        if (wantedProfile is { } wanted)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                if (profiles[i] == wanted)
                    return i;
            }
        }
        return 0;
    }
}
