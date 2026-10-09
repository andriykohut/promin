using Educast.MediaPipeline.Capture;
using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Broadcast;

/// <summary>
/// An encoder that records every picture it is fed and answers from a script: each
/// <see cref="TryEncode"/> either returns the picture at once, holds it back for
/// <see cref="TryTakePending"/>, or refuses it. The encoded bytes are the picture's width.
/// </summary>
internal sealed class FakeVideoEncoder : IVideoEncoder
{
    internal enum Answer { Return, HoldBack, Refuse }

    private readonly Queue<Answer> _script = new();
    private readonly Queue<EncodedFrame> _held = new();
    private uint _frameId;

    public bool UsingHardwareEncoder { get; init; } = true;

    public List<(CapturedFrame Frame, bool ForceKeyframe)> Encoded { get; } = [];

    public int PendingCalls { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>What every call does once the script is used up.</summary>
    public Answer Fallback { get; set; } = Answer.Return;

    public FakeVideoEncoder Then(Answer answer)
    {
        _script.Enqueue(answer);
        return this;
    }

    public bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedFrame encoded)
    {
        Encoded.Add((frame, forceKeyframe));
        var picture = new EncodedFrame(BitConverter.GetBytes(frame.Width), forceKeyframe, _frameId++);
        switch (_script.Count > 0 ? _script.Dequeue() : Fallback)
        {
            case Answer.Return:
                encoded = picture;
                return true;
            case Answer.HoldBack:
                _held.Enqueue(picture);
                break;
        }

        encoded = default;
        return false;
    }

    public bool TryTakePending(out EncodedFrame encoded)
    {
        PendingCalls++;
        return _held.TryDequeue(out encoded);
    }

    public void Dispose() => Disposed = true;
}
