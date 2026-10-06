using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Decoding;

/// <summary>
/// The decoder's input/output accounting over one synchronous transform: when a picture can be handed in,
/// what to do when the transform will not take it, how pictures leave, and what a failure does to the
/// stream. Knows nothing about Media Foundation, so it can be driven by a fake transform. Not thread-safe.
/// <para>
/// A stream starts at a keyframe that carries its parameter sets. After anything goes wrong (bytes that are
/// not H.264, a transform that refuses or fails) the transform is flushed and the stream waits for the next
/// keyframe, because a lost picture would make every later one decode wrongly.
/// </para>
/// <para>
/// What counts as a keyframe is read from the bytes (an IDR slice is present), never from a flag a sender
/// supplied.
/// </para>
/// </summary>
internal sealed class DecoderPipeline<TPicture> : IDisposable where TPicture : class, IDisposable
{
    // A transform that answers every output request with "format changed" is broken, not busy.
    private const int MaxConsecutiveFormatChanges = 16;

    // One input makes one picture; a transform that makes dozens is broken.
    private const int MaxPicturesPerInput = 64;

    private readonly IDecoderMft<TPicture> _mft;
    private bool _awaitingKeyframe = true;
    private bool _transformHoldsStream; // something was accepted since the start or the last flush
    private bool _nextIsDiscontinuity = true;
    private bool _disposed;

    // The newest picture collected during the current call.
    private TPicture? _collected;

    internal DecoderPipeline(IDecoderMft<TPicture> mft) => _mft = mft;

    /// <summary>
    /// What went wrong in the last call, or null when nothing did (including when the call simply had no picture
    /// to show yet, because the decoder held it back). A call that returns a picture can still set this: the
    /// stream may have broken right after that picture, and then <see cref="NeedsKeyframe"/> is true too.
    /// </summary>
    internal string? LastFailure { get; private set; }

    /// <summary>
    /// True from construction until a keyframe has been accepted, and true again after anything that makes the
    /// next delta undecodable (bytes refused, a transform that refused or failed, a flush). A picture held back
    /// does not set it: on a decoder that legitimately holds pictures, re-arming would freeze the stream between keyframes.
    /// </summary>
    internal bool NeedsKeyframe => _awaitingKeyframe;

    /// <summary>Inputs the transform took.</summary>
    internal int InputsAccepted { get; private set; }

    /// <summary>
    /// Calls where the transform took the input and no picture came out of the call (and nothing failed): what a
    /// decoder that is not one-in one-out shows. Refused input, input never offered and failures are not counted.
    /// </summary>
    internal int PicturesHeldBack { get; private set; }

    /// <summary>Pictures that came out of the transform and were released because a newer one came out in the same call.</summary>
    internal int PicturesDropped { get; private set; }

    /// <summary>
    /// Hands the transform one access unit and returns the newest picture that came out of the call, which for
    /// a low-delay decoder is the one just fed. False when none came out: the stream has not started, the bytes
    /// were refused, the decoder failed, or (not a failure) the decoder kept the picture back.
    /// The caller owns the picture and disposes it.
    /// </summary>
    internal bool TryDecode(byte[]? data, out TPicture? picture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        picture = null;
        LastFailure = null;

        try
        {
            Feed(data);
            picture = _collected;
            _collected = null; // handed over: the caller's now
            return picture is not null;
        }
        finally
        {
            // Only when an exception got in the way: a picture collected so far must not hold a pool slot forever.
            _collected?.Dispose();
            _collected = null;
        }
    }

    private void Feed(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            Break("the picture is empty");
            return;
        }

        var summary = H264Stream.Inspect(data);
        if (!summary.IsAnnexB)
        {
            Break("the bytes are not an H.264 Annex B stream");
            return;
        }

        if (_awaitingKeyframe)
        {
            if (!summary.HasIdr)
            {
                LastFailure = "waiting for a keyframe";
                return;
            }
            if (!summary.HasParameterSets)
            {
                LastFailure = "waiting for a keyframe that carries its parameter sets (SPS and PPS)";
                return;
            }
        }

        var result = _mft.ProcessInput(data, summary.HasIdr, _nextIsDiscontinuity);
        if (result == DecoderInputResult.NotAccepting)
        {
            // A transform with a picture waiting refuses more input until it is collected.
            if (!Drain())
                return;
            result = _mft.ProcessInput(data, summary.HasIdr, _nextIsDiscontinuity);
        }

        if (result != DecoderInputResult.Accepted)
        {
            Break($"the decoder refused the picture{Detail()}");
            return;
        }

        _transformHoldsStream = true;
        _nextIsDiscontinuity = false;
        _awaitingKeyframe = false;
        InputsAccepted++;
        if (Drain() && _collected is null)
            PicturesHeldBack++;
    }

    // Collects every picture the transform has ready. False when it failed, in which case the stream is
    // already broken (and whatever was collected before the failure is kept for the caller).
    private bool Drain()
    {
        var formatChanges = 0;
        var pictures = 0;
        while (true)
        {
            switch (_mft.ProcessOutput(out var output))
            {
                case DecoderOutputResult.NeedMoreInput:
                    return true;

                case DecoderOutputResult.Produced:
                    if (output is null)
                        return true;
                    formatChanges = 0;
                    Collect(output);
                    if (++pictures >= MaxPicturesPerInput)
                    {
                        Break("the decoder keeps producing pictures without being given any");
                        return false;
                    }
                    break;

                case DecoderOutputResult.FormatChanged:
                    if (++formatChanges > MaxConsecutiveFormatChanges)
                    {
                        Break("the decoder keeps changing its output format without producing a picture");
                        return false;
                    }
                    break; // a synchronous transform can be asked again at once

                default:
                    Break($"the decoder could not produce the picture{Detail()}");
                    return false;
            }
        }
    }

    private void Collect(TPicture picture)
    {
        if (_collected is { } older)
        {
            older.Dispose();
            PicturesDropped++;
        }
        _collected = picture;
    }

    private string Detail() => _mft.LastError is { Length: > 0 } error ? $": {error}" : "";

    // Something went wrong with this picture: the stream is not reliable until the next keyframe.
    private void Break(string reason)
    {
        LastFailure = reason;
        _awaitingKeyframe = true;
        if (_transformHoldsStream)
        {
            _mft.Flush();
            _transformHoldsStream = false;
        }
        _nextIsDiscontinuity = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _collected?.Dispose();
        _collected = null;
        _mft.Dispose();
    }
}
