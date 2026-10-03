namespace Educast.MediaPipeline.Encoding;

/// <summary>Reads just enough of an H.264 Annex B byte stream to tell what kind of picture it holds.</summary>
internal static class H264Stream
{
    private const int NalIdrSlice = 5;
    private const int NalSps = 7;
    private const int NalPps = 8;

    internal readonly record struct Summary(bool IsAnnexB, bool HasIdr, bool HasSps, bool HasPps)
    {
        public bool HasParameterSets => HasSps && HasPps;
    }

    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    /// <summary>
    /// Scans the NAL units in <paramref name="data"/>. Not Annex B (no start code, or something other
    /// than zero bytes before the first one) is reported as such, with nothing found.
    /// </summary>
    internal static Summary Inspect(ReadOnlySpan<byte> data)
    {
        var isAnnexB = false;
        bool hasIdr = false, hasSps = false, hasPps = false;

        var position = 0;
        var first = true;
        while (position < data.Length)
        {
            var found = data[position..].IndexOf(StartCode);
            if (found < 0)
                break;

            var start = position + found;
            var header = start + StartCode.Length;
            if (header >= data.Length)
                break;

            if (first)
            {
                // Only zero bytes may come before the first start code.
                if (data[..start].IndexOfAnyExcept((byte)0) >= 0)
                    return default;
                isAnnexB = true;
                first = false;
            }

            var nal = data[header];
            if ((nal & 0x80) == 0) // forbidden_zero_bit must be 0
            {
                switch (nal & 0x1F)
                {
                    case NalIdrSlice: hasIdr = true; break;
                    case NalSps: hasSps = true; break;
                    case NalPps: hasPps = true; break;
                }
            }

            position = header + 1;
        }

        return new Summary(isAnnexB, hasIdr, hasSps, hasPps);
    }

    /// <summary>True when <paramref name="block"/> holds both a sequence and a picture parameter set.</summary>
    internal static bool IsParameterSetBlock(ReadOnlySpan<byte> block) => Inspect(block).HasParameterSets;

    /// <summary>
    /// A keyframe must carry its own parameter sets, or a student who joins at it cannot decode it.
    /// Returns <paramref name="frame"/> itself when it is not an IDR picture or already has them;
    /// otherwise a copy with <paramref name="parameterSets"/> in front (when those are usable).
    /// </summary>
    internal static byte[] WithParameterSets(byte[] frame, byte[]? parameterSets)
    {
        if (parameterSets is null || !IsParameterSetBlock(parameterSets))
            return frame;

        var summary = Inspect(frame);
        if (!summary.HasIdr || summary.HasParameterSets)
            return frame;

        var result = new byte[parameterSets.Length + frame.Length];
        parameterSets.CopyTo(result, 0);
        frame.CopyTo(result, parameterSets.Length);
        return result;
    }
}
