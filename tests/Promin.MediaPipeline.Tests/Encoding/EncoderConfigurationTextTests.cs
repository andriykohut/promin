using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Tests.Encoding;

public class EncoderConfigurationTextTests
{
    [Fact]
    public void ASoftwareFallback_NamesEachRejectedHardwareEncoderAndWhy()
    {
        var text = EncoderConfigurationText.Compose(
            [
                ("Intel(R) Quick Sync Video H.264 Encoder MFT", "the encoder refused a forced keyframe (0x80004001)"),
                ("NVIDIA H.264 Encoder MFT", "The encoder refused every H.264 output type: profile=baseline colour=True: 0xC00D36B4"),
            ],
            ["synchronous", "input: memory", "output H.264 baseline"]);

        var lines = text.Split('\n');
        Assert.Contains("rejected hardware encoder Intel(R) Quick Sync Video H.264 Encoder MFT: the encoder refused a forced keyframe (0x80004001)", lines);
        Assert.Contains("rejected hardware encoder NVIDIA H.264 Encoder MFT: The encoder refused every H.264 output type: profile=baseline colour=True: 0xC00D36B4", lines);
        Assert.Equal("synchronous; input: memory; output H.264 baseline", lines[^1]);
    }

    [Fact]
    public void WithNothingRejected_ItIsTheNotesOnOneLine() =>
        Assert.Equal("asynchronous (event-driven); input: Direct3D textures", EncoderConfigurationText.Compose([], ["asynchronous (event-driven)", "input: Direct3D textures"]));

    [Fact]
    public void AReasonThatSpansLines_StaysOnOneLineSoOneLinePerCandidateHolds()
    {
        var text = EncoderConfigurationText.Compose([("X", "first\r\nsecond")], ["n"]);

        Assert.Equal(2, text.Split('\n').Length);
        Assert.Contains("first second", text);
    }
}
