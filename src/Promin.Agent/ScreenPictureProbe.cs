using Promin.MediaPipeline.Pictures;

namespace Promin.Agent;

/// <summary>
/// One log line at start: can this PC's screen be captured from where the agent runs (as SYSTEM, in the signed-in
/// session)? It is how a PC whose tile stays empty on the console is diagnosed.
/// </summary>
internal static class ScreenPictureProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public static string Describe(Func<IScreenPictures> open)
    {
        try
        {
            using var pictures = open();
            return pictures.TryTake(Wait, out var jpeg) switch
            {
                PictureOutcome.Picture => $"Screen pictures work on this PC: the first one is {jpeg.Length / 1024} KB.",
                PictureOutcome.Unavailable =>
                    "Screen pictures are not available right now (the PC is locked, or at a sign-in or UAC prompt). They start when it is unlocked.",
                _ => $"Screen pictures: the screen gave no picture within {Wait.TotalSeconds:0} seconds.",
            };
        }
        catch (Exception failure)
        {
            return $"Screen pictures cannot be taken on this PC: {failure.Message}";
        }
    }
}
