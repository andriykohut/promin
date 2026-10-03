namespace Educast.MediaPipeline.Decoding;

internal enum DecoderInputResult
{
    Accepted,

    /// <summary>The transform will not take input until its pending output is collected (MF_E_NOTACCEPTING).</summary>
    NotAccepting,

    /// <summary>The transform refused the bytes for another reason (corrupt, unsupported, out of sequence).</summary>
    Rejected,
}

internal enum DecoderOutputResult
{
    /// <summary>A finished picture was returned.</summary>
    Produced,

    /// <summary>There is no finished picture (MF_E_TRANSFORM_NEED_MORE_INPUT).</summary>
    NeedMoreInput,

    /// <summary>
    /// The picture format changed (MF_E_TRANSFORM_STREAM_CHANGE) and the new format has been taken up; no
    /// picture came out. The transform can be asked again at once.
    /// </summary>
    FormatChanged,

    /// <summary>The transform failed to produce a picture, or the new format could not be taken up. See <see cref="IDecoderMft{TPicture}.LastError"/>.</summary>
    Failed,
}

/// <summary>
/// What the decoder's input/output accounting needs from a Media Foundation (synchronous) decoder transform,
/// so the accounting can be driven by a fake in tests. <typeparamref name="TPicture"/> is a finished picture;
/// whoever receives one owns it and disposes it.
/// </summary>
internal interface IDecoderMft<TPicture> : IDisposable where TPicture : class, IDisposable
{
    /// <summary>
    /// Hands the transform one access unit. <paramref name="isKeyframe"/> marks one that starts with an IDR
    /// slice; <paramref name="discontinuity"/> marks the first one after a start or a flush. A refusal that
    /// is part of the protocol is a result; anything the bytes can cause is <see cref="DecoderInputResult.Rejected"/>.
    /// </summary>
    DecoderInputResult ProcessInput(byte[] data, bool isKeyframe, bool discontinuity);

    /// <summary>Asks for one finished picture.</summary>
    DecoderOutputResult ProcessOutput(out TPicture? picture);

    /// <summary>Throws away whatever the transform holds: the next picture it takes must be a keyframe.</summary>
    void Flush();

    /// <summary>What the transform last said when it refused or failed, for diagnostics; null when it has not.</summary>
    string? LastError { get; }
}
