using Promin.MediaPipeline.Pictures;

namespace Promin.Agent.Tests;

public class ScreenPictureProbeTests
{
    private sealed class OnePicture(PictureOutcome outcome, byte[] jpeg) : IScreenPictures
    {
        public bool Disposed { get; private set; }

        public PictureOutcome TryTake(TimeSpan timeout, out byte[] picture)
        {
            picture = jpeg;
            return outcome;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void APicture_SaysScreenPicturesWork_AndHowBig()
    {
        var source = new OnePicture(PictureOutcome.Picture, new byte[30 * 1024]);

        var line = ScreenPictureProbe.Describe(() => source);

        Assert.Equal("Screen pictures work on this PC: the first one is 30 KB.", line);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void AScreenThatCannotBeCapturedNow_SaysWhyItMayBe() =>
        Assert.Equal(
            "Screen pictures are not available right now (the PC is locked, or at a sign-in or UAC prompt). They start when it is unlocked.",
            ScreenPictureProbe.Describe(() => new OnePicture(PictureOutcome.Unavailable, [])));

    [Fact]
    public void NoPictureInFiveSeconds_SaysSo() =>
        Assert.Equal(
            "Screen pictures: the screen gave no picture within 5 seconds.",
            ScreenPictureProbe.Describe(() => new OnePicture(PictureOutcome.Unchanged, [])));

    [Fact]
    public void ACaptureThatCannotBeMade_SaysWhy() =>
        Assert.Equal(
            "Screen pictures cannot be taken on this PC: no duplication for you",
            ScreenPictureProbe.Describe(() => throw new InvalidOperationException("no duplication for you")));
}
