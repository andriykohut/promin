using Educast.MediaPipeline.Decoding;

namespace Educast.MediaPipeline.Tests.Decoding;

/// <summary>A finished "picture" from the fake: which input it was made from and the size the fake says it is.</summary>
internal sealed class FakePicture(int index, int width, int height) : IDisposable
{
    public int Index { get; } = index;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// A stand-in for a synchronous decoder transform that follows the rules the pipeline depends on, with
/// switches for the awkward behaviours real decoders have. The "picture" carries the index written into the
/// last byte of its input (see <see cref="Frames"/>) so a test can tell which input an output belongs to.
/// Like a real decoder it will not start without a keyframe: after construction or a flush, anything else
/// is refused (and counted), which lets a test prove the pipeline never offers it.
/// </summary>
internal sealed class FakeDecoderMft : IDecoderMft<FakePicture>
{
    internal sealed record Input(byte[] Data, bool IsKeyframe, bool Discontinuity);

    private readonly Queue<int> _pending = new();
    private readonly Queue<FakePicture> _stale = new();
    private bool _needsKeyframe = true;
    private int _width = 1280;
    private int _height = 720;
    private int _formatChangesToReport;
    private (int Width, int Height)? _sizeAfterFormatChange;
    private bool _rejectNextInput;
    private bool _failNextOutput;
    private int _picturesProduced;

    /// <summary>How many pictures the decoder keeps back: 0 is one-in one-out, 1 is "one behind".</summary>
    public int HoldBack { get; set; }

    /// <summary>Every input produces this many pictures (a misbehaving decoder).</summary>
    public int PicturesPerInput { get; set; } = 1;

    /// <summary>Report a format change on every output request, forever (a broken decoder).</summary>
    public bool AlwaysChangesFormat { get; set; }

    /// <summary>After this many pictures were produced, the next output request fails (null: never).</summary>
    public int? FailOutputAfterPictures { get; set; }

    /// <summary>After this many pictures were produced, the next output request throws (null: never).</summary>
    public int? ThrowOnOutputAfterPictures { get; set; }

    /// <summary>Every picture the fake has handed out, so a test can see which were released.</summary>
    public List<FakePicture> Created { get; } = [];

    public int FlushCount { get; private set; }
    public bool Disposed { get; private set; }
    public List<Input> Inputs { get; } = [];
    public int RefusedForWantOfAKeyframe { get; private set; }
    public string? LastError { get; private set; }

    public void RejectNextInput() => _rejectNextInput = true;

    public void FailNextOutput() => _failNextOutput = true;

    /// <summary>A picture that was ready before the next input: the decoder refuses input until it is collected.</summary>
    public void QueueStaleOutput(int index) => _stale.Enqueue(new FakePicture(index, _width, _height));

    /// <summary>The next output request reports a format change, after which pictures have the new size.</summary>
    public void ChangeFormatBeforeNextPicture(int width, int height)
    {
        _formatChangesToReport = 1;
        _sizeAfterFormatChange = (width, height);
    }

    public DecoderInputResult ProcessInput(byte[] data, bool isKeyframe, bool discontinuity)
    {
        if (_stale.Count > 0)
            return DecoderInputResult.NotAccepting;

        Inputs.Add(new Input(data, isKeyframe, discontinuity));
        if (_rejectNextInput)
        {
            _rejectNextInput = false;
            LastError = "0x80004005 (the bytes were refused)";
            return DecoderInputResult.Rejected;
        }

        if (_needsKeyframe && !isKeyframe)
        {
            RefusedForWantOfAKeyframe++;
            LastError = "no keyframe yet";
            return DecoderInputResult.Rejected;
        }
        _needsKeyframe = false;

        for (var i = 0; i < PicturesPerInput; i++)
            _pending.Enqueue(Frames.IndexOf(data));
        return DecoderInputResult.Accepted;
    }

    public DecoderOutputResult ProcessOutput(out FakePicture? picture)
    {
        picture = null;
        if (AlwaysChangesFormat)
            return DecoderOutputResult.FormatChanged;

        if (ThrowOnOutputAfterPictures is { } throwAfter && _picturesProduced >= throwAfter)
            throw new InvalidOperationException("the fake transform threw");

        if (FailOutputAfterPictures is { } failAfter && _picturesProduced >= failAfter)
        {
            LastError = "0xC00D6D72 (the decoder broke after a picture)";
            return DecoderOutputResult.Failed;
        }

        if (_failNextOutput)
        {
            _failNextOutput = false;
            LastError = "0xC00D6D72 (the decoder broke)";
            return DecoderOutputResult.Failed;
        }

        if (_stale.Count > 0)
        {
            picture = _stale.Dequeue();
            Created.Add(picture);
            _picturesProduced++;
            return DecoderOutputResult.Produced;
        }

        if (_pending.Count <= HoldBack)
            return DecoderOutputResult.NeedMoreInput;

        if (_formatChangesToReport > 0)
        {
            _formatChangesToReport--;
            (_width, _height) = _sizeAfterFormatChange!.Value;
            return DecoderOutputResult.FormatChanged;
        }

        picture = new FakePicture(_pending.Dequeue(), _width, _height);
        Created.Add(picture);
        _picturesProduced++;
        return DecoderOutputResult.Produced;
    }

    public void Flush()
    {
        FlushCount++;
        _pending.Clear();
        _stale.Clear();
        _needsKeyframe = true;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>H.264 Annex B access units, just real enough for the parts that read NAL unit types. The last byte is the index.</summary>
internal static class Frames
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    private static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1F];
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    /// <summary>SPS, PPS, IDR slice: what the encoder's keyframes look like.</summary>
    public static byte[] Keyframe(int index) =>
        [.. StartCode, .. Sps, .. StartCode, .. Pps, .. StartCode, 0x65, 0x88, 0x84, (byte)index];

    /// <summary>An IDR slice and nothing else.</summary>
    public static byte[] KeyframeWithoutParameterSets(int index) => [.. StartCode, 0x65, 0x88, 0x84, (byte)index];

    /// <summary>A non-IDR slice.</summary>
    public static byte[] Delta(int index) => [.. StartCode, 0x41, 0x9A, (byte)index];

    public static int IndexOf(byte[] data) => data[^1];
}
