using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Tests.Encoding;

public class OutputTypeChoiceTests
{
    [Fact]
    public void PrefersTheTypeWithTheProfileTheEncoderWasConfiguredWith() =>
        Assert.Equal(1, OutputTypeChoice.Pick([77, 66, 100], wantedProfile: 66));

    [Fact]
    public void TakesTheFirstOfSeveralMatches() =>
        Assert.Equal(1, OutputTypeChoice.Pick([77, 66, 66], wantedProfile: 66));

    [Fact]
    public void FallsBackToTheFirstOffered_WhenNoneMatches() =>
        Assert.Equal(0, OutputTypeChoice.Pick([77, 100], wantedProfile: 66));

    [Fact]
    public void TakesTheFirstOffered_WhenNoProfileWasConfigured() =>
        Assert.Equal(0, OutputTypeChoice.Pick([77, 66], wantedProfile: null));

    [Fact]
    public void ATypeThatSaysNothingAboutItsProfile_DoesNotMatch() =>
        Assert.Equal(1, OutputTypeChoice.Pick([null, 66], wantedProfile: 66));

    [Fact]
    public void NothingOffered_IsMinusOne() =>
        Assert.Equal(-1, OutputTypeChoice.Pick([], wantedProfile: 66));
}
