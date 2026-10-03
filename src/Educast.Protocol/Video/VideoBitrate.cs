namespace Educast.Protocol.Video;

/// <summary>How much of a picture's detail the teacher wants to keep: more detail costs more bandwidth.</summary>
public enum Sharpness
{
    Light,
    Standard,
    Sharp,
}

public static class VideoBitrate
{
    /// <summary>What the console starts with.</summary>
    public static Sharpness DefaultSharpness => Sharpness.Standard;

    /// <summary>
    /// The bitrate to encode at: width x height x fps x tenths / 10, in 64-bit arithmetic, where
    /// <c>tenths</c> is 1 for Light, 2 for Standard and 3 for Sharp (a tenth of a bit per pixel per frame each).
    /// </summary>
    public static int BitsPerSecond(PixelSize encodeSize, int fps, Sharpness sharpness)
    {
        if (encodeSize.Width <= 0 || encodeSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(encodeSize), encodeSize, "Width and height must be positive.");
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "The frame rate must be positive.");

        long tenths = sharpness switch
        {
            Sharpness.Light => 1,
            Sharpness.Standard => 2,
            Sharpness.Sharp => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(sharpness), sharpness, "Not one of the three sharpness choices."),
        };

        var bits = (long)encodeSize.Width * encodeSize.Height * fps * tenths / 10;
        if (bits > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(encodeSize), encodeSize, "The bitrate for this size and frame rate does not fit a 32-bit number.");
        return (int)bits;
    }
}
