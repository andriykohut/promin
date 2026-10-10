namespace Promin.MediaPipeline.Encoding;

internal enum InputResult
{
    Accepted,

    /// <summary>The transform will not take a picture now (MF_E_NOTACCEPTING).</summary>
    NotAccepting,
}

internal enum OutputResult
{
    /// <summary>A finished picture was returned.</summary>
    Produced,

    /// <summary>There is no finished picture (MF_E_TRANSFORM_NEED_MORE_INPUT).</summary>
    NeedMoreInput,

    /// <summary>
    /// The transform changed its output format (MF_E_TRANSFORM_STREAM_CHANGE) and it has been renegotiated;
    /// no picture came out. A synchronous transform can be asked again at once; an asynchronous one must be
    /// asked again only after its next have-output event.
    /// </summary>
    FormatChanged,
}

internal enum MftEvent
{
    /// <summary>Nothing is waiting in the event queue.</summary>
    None,

    /// <summary>METransformNeedInput: the transform will take one more picture.</summary>
    NeedInput,

    /// <summary>METransformHaveOutput: one finished picture can be collected.</summary>
    HaveOutput,

    /// <summary>Any other event, of no interest here.</summary>
    Other,
}

/// <summary>One finished picture exactly as the transform produced it.</summary>
internal readonly record struct RawOutput(byte[] Data, bool IsCleanPoint);

/// <summary>How long the pipeline waits for an asynchronous encoder.</summary>
internal readonly record struct EncoderTimeouts(TimeSpan Input, TimeSpan Output)
{
    /// <summary>Two seconds for the encoder to ask for a picture, half a second for the picture just fed to come out.</summary>
    internal static EncoderTimeouts Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));
}

/// <summary>
/// What the encoder's input/output accounting needs from a Media Foundation transform, so that the accounting
/// can be driven by a fake in tests. <typeparamref name="TInput"/> is the picture handed over (an
/// <c>IMFSample</c> for the real transform).
/// </summary>
internal interface IEncoderMft<in TInput> : IDisposable
{
    /// <summary>Event-driven (usual for hardware) as opposed to synchronous.</summary>
    bool IsAsynchronous { get; }

    /// <summary>Hands the transform a picture. Anything but a refusal that is part of the protocol throws.</summary>
    InputResult ProcessInput(TInput input);

    /// <summary>Asks for one finished picture.</summary>
    OutputResult ProcessOutput(out RawOutput output);

    /// <summary>
    /// The next event, without waiting; <see cref="MftEvent.None"/> when the queue is empty and always for a
    /// synchronous transform. An event that reports an error throws.
    /// </summary>
    MftEvent PollEvent();

    /// <summary>Asks for the next picture handed over to be a keyframe. Throws when the transform refuses.</summary>
    void RequestKeyframe();

    /// <summary>
    /// The sequence and picture parameter sets (an Annex B block with both) the transform currently publishes in its
    /// output type, or null when it publishes none. Read afresh each time: it can change after a format change,
    /// and some encoders only publish it once streaming has begun.
    /// </summary>
    byte[]? ReadParameterSets();
}
