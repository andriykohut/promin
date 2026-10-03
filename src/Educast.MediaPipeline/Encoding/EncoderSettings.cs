namespace Educast.MediaPipeline.Encoding;

/// <summary>The numbers the encoder is configured with, kept apart so they can be tested without a GPU.</summary>
internal static class EncoderSettings
{
    internal const uint MinimumBitrate = 500_000;
    internal const uint MaximumBitrate = 10_000_000;

    // Bits per pixel per frame. Screen content is mostly still and sharp-edged; this is a starting
    // point to be tuned on real hardware, not a measured figure.
    private const long BitsPerPixelDenominator = 10; // 0.1 bit per pixel per frame

    private const int KeyframeIntervalSeconds = 5;
    private const long HundredNanosecondsPerSecond = 10_000_000;

    internal static uint BitrateFor(int width, int height, int fps)
    {
        var bits = (long)width * height * fps / BitsPerPixelDenominator;
        return (uint)Math.Clamp(bits, MinimumBitrate, MaximumBitrate);
    }

    /// <summary>
    /// The longest the encoder may go without a keyframe of its own, in frames. A student who missed one
    /// recovers within this even if nobody asks for a keyframe.
    /// </summary>
    internal static uint KeyframeIntervalFor(int fps) => (uint)(fps * KeyframeIntervalSeconds);

    /// <summary>The presentation time of frame <paramref name="index"/> at a steady <paramref name="fps"/>, in 100 ns units.</summary>
    internal static long SampleTime(long index, int fps) => index * HundredNanosecondsPerSecond / fps;

    internal static long SampleDuration(int fps) => HundredNanosecondsPerSecond / fps;

    /// <summary>Media Foundation's way of storing two 32-bit numbers (width and height, numerator and denominator) in one UINT64.</summary>
    internal static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
