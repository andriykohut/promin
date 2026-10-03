using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Video;

public class QualityPresetTests
{
    [Fact]
    public void All_ListsTheFourChoices_InTheOrderThePickerShowsThem() =>
        Assert.Equal(
            ["1080p, 15 fps", "1080p, 10 fps", "720p, 15 fps", "720p, 10 fps"],
            QualityPreset.All.Select(p => p.Label));

    [Fact]
    public void Default_Is1080pAt15()
    {
        Assert.Equal(new PixelSize(1920, 1080), QualityPreset.Default.Ceiling);
        Assert.Equal(15, QualityPreset.Default.Fps);
    }

    [Fact]
    public void SoftwareFallback_Is720pAt10()
    {
        Assert.Equal(new PixelSize(1280, 720), QualityPreset.SoftwareFallback.Ceiling);
        Assert.Equal(10, QualityPreset.SoftwareFallback.Fps);
    }
}
