namespace Educast.MediaPipeline.Encoding;

/// <summary>The numbers the encoder is configured with, kept apart so they can be tested without a GPU.</summary>
internal static class EncoderSettings
{
    private const long HundredNanosecondsPerSecond = 10_000_000;

    /// <summary>
    /// The longest spacing between the encoder's own keyframes, to try in order until the encoder accepts one
    /// (the property is a count of pictures). The broadcast session decides when a keyframe is wanted and forces
    /// one; an encoder that adds keyframes of its own only costs bandwidth.
    /// </summary>
    internal static uint[] KeyframeSpacingLadder { get; } = [uint.MaxValue, int.MaxValue, 1_000_000, 100_000, 10_000, 1_000];

    /// <summary>The presentation time of frame <paramref name="index"/> at a steady <paramref name="fps"/>, in 100 ns units.</summary>
    internal static long SampleTime(long index, int fps) => index * HundredNanosecondsPerSecond / fps;

    internal static long SampleDuration(int fps) => HundredNanosecondsPerSecond / fps;

    /// <summary>Media Foundation's way of storing two 32-bit numbers (width and height, numerator and denominator) in one UINT64.</summary>
    internal static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
