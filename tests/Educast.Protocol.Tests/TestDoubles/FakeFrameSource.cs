using Educast.Protocol.Broadcast;

namespace Educast.Protocol.Tests.TestDoubles;

/// <summary>What the fake source does on one call.</summary>
public enum SourceStep
{
    /// <summary>The picture changed: a new frame; a keyframe only if one was forced.</summary>
    Changed,

    /// <summary>The picture changed and the encoder chose a keyframe on its own.</summary>
    ChangedAsKeyframe,

    /// <summary>The picture changed; the encoder produced a delta even though a keyframe was forced.</summary>
    ChangedIgnoringForce,

    /// <summary>Nothing changed, and not even a forced keyframe is honoured (no picture yet).</summary>
    NothingNew,

    /// <summary>Nothing changed, but a forced keyframe re-encodes the current picture.</summary>
    Still,

    /// <summary>The source fails.</summary>
    Throw,
}

/// <summary>One recorded call to <see cref="FakeFrameSource"/>.</summary>
public readonly record struct SourceCall(int Index, bool ForceKeyframe, bool Produced, bool IsKeyframe);

/// <summary>
/// An <see cref="IEncodedFrameSource"/> that follows a script (one <see cref="SourceStep"/> per call),
/// then keeps doing <see cref="Fallback"/>, and records every call it receives.
/// Frame <c>n</c> carries the single byte <c>n</c>; re-encoding a still picture repeats its byte.
/// </summary>
public sealed class FakeFrameSource : IEncodedFrameSource
{
    private readonly object _lock = new();
    private readonly Queue<SourceStep> _script;
    private readonly List<SourceCall> _calls = [];
    private int _picturesProduced;
    private byte[]? _picture;

    public FakeFrameSource(SourceStep fallback, params SourceStep[] script)
    {
        Fallback = fallback;
        _script = new Queue<SourceStep>(script);
    }

    /// <summary>What every call does once the script is used up.</summary>
    public SourceStep Fallback { get; set; }

    /// <summary>What <see cref="SourceStep.Throw"/> throws.</summary>
    public Exception FailureToThrow { get; set; } = new InvalidOperationException("scripted source failure");

    /// <summary>
    /// When true, a call that returns false sleeps for the timeout it was given first,
    /// as a real capture does while it waits for the screen to change.
    /// </summary>
    public bool BlockForTimeoutWhenNothingNew { get; set; }

    public int CallCount
    {
        get { lock (_lock) { return _calls.Count; } }
    }

    /// <summary>How many calls returned a frame.</summary>
    public int ProducedCount
    {
        get { lock (_lock) { return _calls.Count(c => c.Produced); } }
    }

    public IReadOnlyList<SourceCall> Calls
    {
        get { lock (_lock) { return _calls.ToArray(); } }
    }

    public bool TryGetNextFrame(TimeSpan timeout, bool forceKeyframe, out EncodedVideoFrame frame)
    {
        bool produced;
        bool isKeyframe = false;
        byte[] data = [];
        lock (_lock)
        {
            var step = _script.Count > 0 ? _script.Dequeue() : Fallback;
            switch (step)
            {
                case SourceStep.Changed:
                    (produced, isKeyframe, data) = (true, forceKeyframe, NewPicture());
                    break;
                case SourceStep.ChangedAsKeyframe:
                    (produced, isKeyframe, data) = (true, true, NewPicture());
                    break;
                case SourceStep.ChangedIgnoringForce:
                    (produced, isKeyframe, data) = (true, false, NewPicture());
                    break;
                case SourceStep.Still when forceKeyframe && _picture is not null:
                    (produced, isKeyframe, data) = (true, true, _picture);
                    break;
                case SourceStep.Throw:
                    _calls.Add(new SourceCall(_calls.Count, forceKeyframe, false, false));
                    throw FailureToThrow;
                default:
                    produced = false;
                    break;
            }

            _calls.Add(new SourceCall(_calls.Count, forceKeyframe, produced, isKeyframe));
        }

        if (!produced && BlockForTimeoutWhenNothingNew)
        {
            Thread.Sleep(timeout);
        }

        frame = produced ? new EncodedVideoFrame(data, isKeyframe) : default;
        return produced;
    }

    private byte[] NewPicture()
    {
        _picture = [(byte)_picturesProduced++];
        return _picture;
    }
}
