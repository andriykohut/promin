namespace Promin.Protocol.Video;

/// <summary>One choice in the console's quality picker. The size is a ceiling, not a target.</summary>
public sealed record QualityPreset(string Label, PixelSize Ceiling, int Fps)
{
    public static QualityPreset Hd15 { get; } = new("1080p, 15 fps", new PixelSize(1920, 1080), 15);
    public static QualityPreset Hd10 { get; } = new("1080p, 10 fps", new PixelSize(1920, 1080), 10);
    public static QualityPreset Sd15 { get; } = new("720p, 15 fps", new PixelSize(1280, 720), 15);
    public static QualityPreset Sd10 { get; } = new("720p, 10 fps", new PixelSize(1280, 720), 10);

    public static IReadOnlyList<QualityPreset> All { get; } = [Hd15, Hd10, Sd15, Sd10];

    public static QualityPreset Default => Hd15;

    /// <summary>What a machine with no hardware H.264 encoder is held to.</summary>
    public static QualityPreset SoftwareFallback => Sd10;
}
