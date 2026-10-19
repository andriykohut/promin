using Promin.MediaPipeline.Pictures;

namespace Promin.MediaPipeline.Tests.Pictures;

// Needs an interactive, unlocked desktop. Reads the screen; moves nothing. Run by hand (remove the Skip, run, put it back).
public class ScreenPicturesTests(ITestOutputHelper output)
{
    private const string NeedsDesktop = "Requires an interactive desktop session with a GPU; reads the screen; run manually on target hardware.";

    [Fact(Skip = NeedsDesktop)]
    public void AFreshSource_GivesAJpeg_ThatFitsTheBounds()
    {
        using var pictures = new ScreenPictures();

        var outcome = pictures.TryTake(TimeSpan.FromSeconds(5), out var jpeg);

        Assert.Equal(PictureOutcome.Picture, outcome);
        Assert.True(jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8);
        Assert.InRange(jpeg.Length, 1, 1024 * 1024);
        output.WriteLine($"First picture: {jpeg.Length} bytes.");
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "promin-screen-picture.jpg"), jpeg);
        output.WriteLine($"Written to {Path.Combine(Path.GetTempPath(), "promin-screen-picture.jpg")} for a look.");
    }
}
