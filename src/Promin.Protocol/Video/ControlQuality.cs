namespace Promin.Protocol.Video;

/// <summary>The quality of the video of a PC being controlled: sharp text matters more than motion, and a software encoder is held to what it can sustain.</summary>
public static class ControlQuality
{
    public static QualityPreset Hardware { get; } = new("1080p, 30 fps", new PixelSize(1920, 1080), 30);

    public static QualityPreset Software { get; } = new("720p, 15 fps", new PixelSize(1280, 720), 15);
}
