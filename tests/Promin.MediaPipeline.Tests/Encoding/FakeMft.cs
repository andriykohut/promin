using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Tests.Encoding;

/// <summary>
/// A stand-in for an encoder transform that follows the Media Foundation rules the pipeline depends on, with
/// switches for the awkward behaviours real encoders have. The "picture" is just the input's index, which is
/// written into the bytes it produces so a test can tell which input an output belongs to.
/// Asynchronous mode refuses what an asynchronous transform refuses: input without a NeedInput event, and a
/// ProcessOutput call that no HaveOutput event asked for (that is E_UNEXPECTED on the real thing).
/// </summary>
internal sealed class FakeMft : IEncoderMft<int>
{
    internal enum ParameterSetPolicy { WithEveryKeyframe, WithTheFirstKeyframeOnly, Never }

    private static readonly byte[] StartCode = [0, 0, 0, 1];
    internal static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1F];
    internal static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    private readonly Queue<InFlight> _inFlight = new();
    private int _needInputToDeliver;
    private int _tokens;           // asynchronous: NeedInput events delivered and not yet used
    private int _announced;        // asynchronous: HaveOutput events delivered and not yet answered by ProcessOutput
    private bool _keyframeRequested;
    private bool _firstKeyframeEmitted;

    private sealed class InFlight(int index, bool keyframe, bool held)
    {
        public int Index { get; } = index;
        public bool Keyframe { get; } = keyframe;
        public bool Held { get; set; } = held;
        public bool Announced { get; set; }
    }

    public FakeMft(bool asynchronous)
    {
        IsAsynchronous = asynchronous;
        if (asynchronous)
            _needInputToDeliver = 1;
    }

    public bool IsAsynchronous { get; }

    /// <summary>Which pictures, by input index, stay unfinished until <see cref="Release"/> is called.</summary>
    public Func<int, bool> HeldBack { get; set; } = _ => false;

    /// <summary>Finishes every picture handed in so far that <see cref="HeldBack"/> kept back.</summary>
    public void Release()
    {
        foreach (var picture in _inFlight)
            picture.Held = false;
    }

    /// <summary>Asynchronous: ask for the next picture only once a finished one has been collected (a depth-one encoder).</summary>
    public bool AsksForInputOnlyAfterOutputIsTaken { get; set; }

    /// <summary>Asynchronous: after the first picture was accepted, never ask for another.</summary>
    public bool NeverAsksForMoreInput { get; set; }

    /// <summary>Synchronous: refuse input while a finished picture is waiting to be collected.</summary>
    public bool RefusesInputWhileOutputWaits { get; set; }

    /// <summary>Report a format change instead of the first picture.</summary>
    public bool ChangesFormatOnFirstOutput { get; set; }

    /// <summary>The header (an Annex B SPS+PPS block) the output type publishes now; tests set it to publish one late.</summary>
    public byte[]? PublishedHeader { get; set; }

    /// <summary>The header the output type publishes once the format has changed.</summary>
    public byte[]? HeaderPublishedByFormatChange { get; set; }

    /// <summary>Asynchronous: refuse an input the transform itself asked for (a transform breaking the protocol).</summary>
    public bool RefusesInputItAskedFor { get; set; }

    public ParameterSetPolicy ParameterSets { get; set; } = ParameterSetPolicy.WithEveryKeyframe;

    public int ProcessOutputCalls { get; private set; }

    public int KeyframeRequests { get; private set; }

    public bool Disposed { get; private set; }

    public InputResult ProcessInput(int input)
    {
        if (IsAsynchronous)
        {
            if (_tokens == 0 || RefusesInputItAskedFor)
                return InputResult.NotAccepting;
            _tokens--;
        }
        else if (RefusesInputWhileOutputWaits && _inFlight.Count > 0)
        {
            return InputResult.NotAccepting;
        }

        var keyframe = _keyframeRequested || input == 0;
        _keyframeRequested = false;
        _inFlight.Enqueue(new InFlight(input, keyframe, HeldBack(input)));

        if (IsAsynchronous && !AsksForInputOnlyAfterOutputIsTaken && !NeverAsksForMoreInput)
            _needInputToDeliver++;
        return InputResult.Accepted;
    }

    public OutputResult ProcessOutput(out RawOutput output)
    {
        ProcessOutputCalls++;
        output = default;

        if (IsAsynchronous)
        {
            if (_announced == 0)
                throw new InvalidOperationException("E_UNEXPECTED: ProcessOutput was called without a have-output event.");
            _announced--;
        }
        else if (_inFlight.Count == 0 || _inFlight.Peek().Held)
        {
            return OutputResult.NeedMoreInput;
        }

        if (ChangesFormatOnFirstOutput)
        {
            ChangesFormatOnFirstOutput = false;
            if (HeaderPublishedByFormatChange is not null)
                PublishedHeader = HeaderPublishedByFormatChange;
            // The transform will announce the same picture again once its new format is in place.
            if (IsAsynchronous && _inFlight.Count > 0)
                _inFlight.Peek().Announced = false;
            return OutputResult.FormatChanged;
        }

        var picture = _inFlight.Dequeue();
        output = new RawOutput(Build(picture), IsCleanPoint: picture.Keyframe);
        if (IsAsynchronous && AsksForInputOnlyAfterOutputIsTaken && !NeverAsksForMoreInput)
            _needInputToDeliver++;
        return OutputResult.Produced;
    }

    public MftEvent PollEvent()
    {
        if (!IsAsynchronous)
            return MftEvent.None;

        if (_needInputToDeliver > 0)
        {
            _needInputToDeliver--;
            _tokens++;
            return MftEvent.NeedInput;
        }

        if (_inFlight.Count > 0)
        {
            var head = _inFlight.Peek();
            if (!head.Announced && !head.Held)
            {
                head.Announced = true;
                _announced++;
                return MftEvent.HaveOutput;
            }
        }
        return MftEvent.None;
    }

    public void RequestKeyframe()
    {
        KeyframeRequests++;
        _keyframeRequested = true;
    }

    public byte[]? ReadParameterSets() => PublishedHeader;

    /// <summary>An Annex B SPS+PPS block whose bytes carry <paramref name="marker"/>, so two headers can be told apart.</summary>
    internal static byte[] Header(byte marker) =>
        [.. StartCode, 0x67, 0x42, 0x00, marker, .. StartCode, 0x68, 0xCE, marker];

    public void Dispose() => Disposed = true;

    /// <summary>The index of the input a picture was made from.</summary>
    internal static int IndexOf(byte[] data) => data[^1];

    private byte[] Build(InFlight picture)
    {
        var bytes = new List<byte>();
        if (picture.Keyframe)
        {
            var withSets = ParameterSets switch
            {
                ParameterSetPolicy.WithEveryKeyframe => true,
                ParameterSetPolicy.WithTheFirstKeyframeOnly => !_firstKeyframeEmitted,
                _ => false,
            };
            if (withSets)
            {
                bytes.AddRange(StartCode); bytes.AddRange(Sps);
                bytes.AddRange(StartCode); bytes.AddRange(Pps);
            }
            _firstKeyframeEmitted = true;
            bytes.AddRange(StartCode); bytes.AddRange(new byte[] { 0x65, 0x88, 0x84 });
        }
        else
        {
            bytes.AddRange(StartCode); bytes.AddRange(new byte[] { 0x41, 0x9A });
        }
        bytes.Add((byte)picture.Index); // always the last byte
        return [.. bytes];
    }
}
