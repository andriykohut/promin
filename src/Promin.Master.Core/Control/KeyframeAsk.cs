namespace Promin.Master.Core.Control;

/// <summary>
/// Decides when the console PC asks the agent PC for a keyframe: when a frame id skips one (a lost frame) or the
/// decoder cannot go on without one, and never more than once every 500 ms, so the ask is not repeated while
/// the keyframe is still on its way.
/// </summary>
public sealed class KeyframeAsk
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(500);

    private uint? _previousFrameId;
    private DateTimeOffset? _lastAsk;

    /// <summary>True when a keyframe should be asked for now. Call once per received frame, in arrival order.</summary>
    public bool ShouldAsk(uint frameId, bool decoderNeedsKeyframe, DateTimeOffset now)
    {
        var gap = _previousFrameId is { } previous && frameId != previous + 1;
        _previousFrameId = frameId;

        if (!gap && !decoderNeedsKeyframe)
        {
            return false;
        }

        if (_lastAsk is { } last && now - last < MinimumGap)
        {
            return false;
        }

        _lastAsk = now;
        return true;
    }
}
