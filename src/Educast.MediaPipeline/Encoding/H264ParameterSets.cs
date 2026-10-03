namespace Educast.MediaPipeline.Encoding;

/// <summary>
/// Remembers the sequence and picture parameter sets of the stream and gives them to a keyframe that
/// arrives without. A student who joins at a keyframe can only decode it if it carries them.
/// </summary>
internal sealed class H264ParameterSets
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private byte[]? _sps;
    private byte[]? _pps;

    /// <summary>
    /// Starts from the parameter sets the encoder published in its output type (an Annex B block with both),
    /// until the stream shows its own. Anything that is not such a block is ignored.
    /// </summary>
    /// <summary>True when both a sequence and a picture parameter set are remembered.</summary>
    public bool KnowsBoth => _sps is not null && _pps is not null;

    public void Seed(byte[]? annexBBlock)
    {
        if (annexBBlock is not null && H264Stream.IsParameterSetBlock(annexBBlock))
            Observe(annexBBlock);
    }

    /// <summary>
    /// Takes the sequence and picture parameter sets out of <paramref name="frame"/>, replacing the
    /// remembered ones when they differ. A frame that is not Annex B, or has none, changes nothing.
    /// </summary>
    public void Observe(ReadOnlySpan<byte> frame)
    {
        foreach (var unit in H264Stream.NalUnits(frame))
        {
            if (unit.Type is not (H264Stream.NalSps or H264Stream.NalPps))
                continue;

            byte[] normalized = [.. StartCode, .. frame[unit.Header..unit.End]];
            if (unit.Type == H264Stream.NalSps)
                _sps = normalized;
            else
                _pps = normalized;
        }
    }

    /// <summary>
    /// For a keyframe (an Annex B picture with an IDR slice) that lacks a sequence or picture parameter set:
    /// the remembered SPS and PPS are put in front of it. Returns <paramref name="frame"/> itself, with
    /// <paramref name="added"/> false, for any other picture and for a keyframe that has both.
    /// Throws when a keyframe needs them and none are known: sending it would give students a picture they
    /// cannot decode, and say nothing.
    /// </summary>
    public byte[] Complete(byte[] frame, out bool added)
    {
        added = false;
        var summary = H264Stream.Inspect(frame);
        if (!summary.IsAnnexB || !summary.HasIdr || summary.HasParameterSets)
            return frame;

        if (_sps is null || _pps is null)
        {
            throw new InvalidOperationException(
                "The encoder produced a keyframe without parameter sets (SPS/PPS), and none were seen earlier in the stream " +
                "or in its output type, so a student could not decode it.");
        }

        added = true;
        return [.. _sps, .. _pps, .. frame];
    }
}
