using System.Diagnostics;
using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

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

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<InFlight> _inFlight = new();
    private int _needInputToDeliver;
    private int _tokens;           // asynchronous: NeedInput events delivered and not yet used
    private int _announced;        // asynchronous: HaveOutput events delivered and not yet answered by ProcessOutput
    private bool _keyframeRequested;
    private bool _firstKeyframeEmitted;

    private sealed class InFlight(int index, bool keyframe, long readyAtMs)
    {
        public int Index { get; } = index;
        public bool Keyframe { get; } = keyframe;
        public long ReadyAtMs { get; } = readyAtMs;
        public bool Announced { get; set; }
    }

    public FakeMft(bool asynchronous)
    {
        IsAsynchronous = asynchronous;
        if (asynchronous)
            _needInputToDeliver = 1;
    }

    public bool IsAsynchronous { get; }

    /// <summary>How long after its input a picture takes to be ready, by input index.</summary>
    public Func<int, TimeSpan> DelayFor { get; set; } = _ => TimeSpan.Zero;

    /// <summary>Asynchronous: ask for the next picture only once a finished one has been collected (a depth-one encoder).</summary>
    public bool AsksForInputOnlyAfterOutputIsTaken { get; set; }

    /// <summary>Asynchronous: after the first picture was accepted, never ask for another.</summary>
    public bool NeverAsksForMoreInput { get; set; }

    /// <summary>Synchronous: refuse input while a finished picture is waiting to be collected.</summary>
    public bool RefusesInputWhileOutputWaits { get; set; }

    /// <summary>Report a format change instead of the first picture.</summary>
    public bool ChangesFormatOnFirstOutput { get; set; }

    public ParameterSetPolicy ParameterSets { get; set; } = ParameterSetPolicy.WithEveryKeyframe;

    public int ProcessOutputCalls { get; private set; }

    public int KeyframeRequests { get; private set; }

    public bool Disposed { get; private set; }

    public InputResult ProcessInput(int input)
    {
        if (IsAsynchronous)
        {
            if (_tokens == 0)
                return InputResult.NotAccepting;
            _tokens--;
        }
        else if (RefusesInputWhileOutputWaits && _inFlight.Count > 0)
        {
            return InputResult.NotAccepting;
        }

        var keyframe = _keyframeRequested || input == 0;
        _keyframeRequested = false;
        _inFlight.Enqueue(new InFlight(input, keyframe, _clock.ElapsedMilliseconds + (long)DelayFor(input).TotalMilliseconds));

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
        else if (_inFlight.Count == 0 || _inFlight.Peek().ReadyAtMs > _clock.ElapsedMilliseconds)
        {
            return OutputResult.NeedMoreInput;
        }

        if (ChangesFormatOnFirstOutput)
        {
            ChangesFormatOnFirstOutput = false;
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
            if (!head.Announced && head.ReadyAtMs <= _clock.ElapsedMilliseconds)
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
