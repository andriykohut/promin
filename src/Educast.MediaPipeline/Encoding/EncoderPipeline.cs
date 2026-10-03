using System.Diagnostics;

namespace Educast.MediaPipeline.Encoding;

/// <summary>
/// The encoder's input/output accounting over one transform: when a picture can be handed in, when one has
/// come out, how asynchronous and synchronous transforms differ, and how the pictures leave as
/// <see cref="EncodedFrame"/>s. Knows nothing about Media Foundation, so it can be driven by a fake transform.
/// Not thread-safe.
/// </summary>
internal sealed class EncoderPipeline<TInput>
{
    // A synchronous transform that answers every output request with "format changed" is broken, not busy.
    private const int MaxConsecutiveFormatChanges = 16;

    private readonly IEncoderMft<TInput> _mft;
    private readonly EncoderTimeouts _timeouts;
    private readonly H264ParameterSets _parameterSets;

    // Asynchronous transforms only: requests for input and finished pictures announced by events and not yet used.
    private int _inputRequests;
    private int _outputsReady;

    // Pictures taken from the transform and not yet handed out, oldest first.
    private readonly Queue<RawOutput> _ready = new();
    private uint _nextFrameId;

    internal EncoderPipeline(IEncoderMft<TInput> mft, EncoderTimeouts timeouts, H264ParameterSets parameterSets)
    {
        _mft = mft;
        _timeouts = timeouts;
        _parameterSets = parameterSets;
    }

    /// <summary>How many pictures the transform has taken. The encoder numbers its sample times with it, so a dropped picture leaves no gap.</summary>
    internal long InputsAccepted { get; private set; }

    /// <summary>Keyframes that came out of the encoder without parameter sets of their own and were given remembered ones.</summary>
    internal int KeyframesCompleted { get; private set; }

    /// <summary>
    /// Hands the transform a picture and returns the oldest finished picture, which is usually the one just
    /// fed. False when none is ready: the encoder took longer than the output wait (the picture is not lost;
    /// <see cref="TryTakePending"/> or a later call returns it) or would not take this one in time.
    /// A finished picture is returned even when the new one was refused.
    /// </summary>
    internal bool TryEncode(TInput input, bool forceKeyframe, out EncodedFrame encoded)
    {
        var accepted = TryInput(input, forceKeyframe);

        // Waiting for the new picture is only worth it when no older one is already in hand: the older one
        // is the one handed out, so the wait would buy nothing but delay.
        Collect(waitForOne: accepted && _ready.Count == 0);
        return TryTakeFrame(out encoded);
    }

    /// <summary>Returns a finished picture without feeding a new one. Never waits. False when nothing is ready.</summary>
    internal bool TryTakePending(out EncodedFrame encoded)
    {
        Collect(waitForOne: false);
        return TryTakeFrame(out encoded);
    }

    // ---- input ----------------------------------------------------------------------------

    private bool TryInput(TInput input, bool forceKeyframe)
    {
        if (_mft.IsAsynchronous)
        {
            // Some encoders only ask for the next picture once the finished one has been taken, so finished
            // pictures met while waiting are taken: otherwise this wait could only time out.
            if (!WaitUntil(AsksForInput, _timeouts.Input))
                return false;
            _inputRequests--;
        }

        // The request is made for the picture about to be handed over. If the transform then refuses it,
        // the request stays with the transform and applies to the next picture it takes.
        if (forceKeyframe)
            _mft.RequestKeyframe();

        if (_mft.ProcessInput(input) == InputResult.Accepted)
        {
            InputsAccepted++;
            return true;
        }

        if (_mft.IsAsynchronous)
        {
            // It asked for this picture and now refuses it: the transform is breaking the protocol. Saying so is
            // better than every later call retrying without an event and returning false, silently.
            throw new InvalidOperationException("The encoder refused a picture it had asked for (MF_E_NOTACCEPTING after a need-input event).");
        }

        // A synchronous transform with a picture waiting refuses more input until it is collected.
        DrainSynchronous();
        if (_mft.ProcessInput(input) != InputResult.Accepted)
            return false;
        InputsAccepted++;
        return true;
    }

    private bool AsksForInput()
    {
        if (_outputsReady > 0)
            DrainAnnounced();
        return _inputRequests > 0;
    }

    // ---- output ---------------------------------------------------------------------------

    private void Collect(bool waitForOne)
    {
        if (!_mft.IsAsynchronous)
        {
            DrainSynchronous();
            return;
        }

        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (waitForOne)
                WaitUntil(() => _outputsReady > 0, _timeouts.Output - clock.Elapsed);
            else
                PumpEvents();

            var formatChanged = DrainAnnounced();

            // After a format change the transform announces the picture again once it is ready for the new
            // format: that is the next event, not a reason to ask now. Keep waiting for it, within the same limit.
            var stillWaiting = waitForOne && formatChanged && _ready.Count == 0 && clock.Elapsed < _timeouts.Output;
            if (!stillWaiting)
                return;
        }
    }

    // Asks for one picture for each have-output event received, and no more: an asynchronous transform
    // may only be asked in answer to one. Returns whether a format change was met.
    private bool DrainAnnounced()
    {
        var formatChanged = false;
        while (_outputsReady > 0)
        {
            _outputsReady--;
            switch (_mft.ProcessOutput(out var output))
            {
                case OutputResult.Produced:
                    _ready.Enqueue(output);
                    break;
                case OutputResult.FormatChanged:
                    formatChanged = true;
                    ReseedParameterSets();
                    break;
            }
        }
        return formatChanged;
    }

    private void DrainSynchronous()
    {
        var formatChanges = 0;
        while (true)
        {
            switch (_mft.ProcessOutput(out var output))
            {
                case OutputResult.NeedMoreInput:
                    return;
                case OutputResult.Produced:
                    _ready.Enqueue(output);
                    formatChanges = 0;
                    break;
                case OutputResult.FormatChanged:
                    ReseedParameterSets();
                    if (++formatChanges > MaxConsecutiveFormatChanges)
                        throw new InvalidOperationException("The encoder keeps changing its output format without producing a picture.");
                    break; // a synchronous transform can be asked again at once
            }
        }
    }

    private bool TryTakeFrame(out EncodedFrame encoded)
    {
        encoded = default;
        if (!_ready.TryDequeue(out var raw))
            return false;

        var data = raw.Data;
        var summary = H264Stream.Inspect(data);

        // What the encoder really produced decides, not what was asked for. Without an Annex B stream to
        // read (it should always be one), fall back on the transform's own "clean point" mark.
        var isKeyframe = summary.IsAnnexB ? summary.HasIdr : raw.IsCleanPoint;
        if (summary.IsAnnexB)
            _parameterSets.Observe(data);
        if (isKeyframe)
        {
            // An encoder may publish its header late (only once streaming has begun): look once more before
            // giving up on a keyframe that has none of its own.
            if (!summary.HasParameterSets && !_parameterSets.KnowsBoth)
                ReseedParameterSets();
            data = _parameterSets.Complete(data, out var added);
            if (added)
                KeyframesCompleted++;
        }

        encoded = new EncodedFrame(data, isKeyframe, _nextFrameId++);
        return true;
    }

    // The output type is where an encoder publishes its parameter sets, and it changes with the format: what the
    // transform publishes now replaces what was remembered. Sets seen later in the stream replace it in turn.
    private void ReseedParameterSets() => _parameterSets.Seed(_mft.ReadParameterSets());

    // ---- waiting ----------------------------------------------------------------------------

    private bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        PumpEvents();
        if (condition())
            return true;

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            // Hardware answers in a few milliseconds: yield for the first of them, then sleep so a
            // stalled transform does not burn a core.
            if (clock.ElapsedMilliseconds < 3)
                Thread.Yield();
            else
                Thread.Sleep(1);

            PumpEvents();
            if (condition())
                return true;
        }
        return false;
    }

    private void PumpEvents()
    {
        while (true)
        {
            switch (_mft.PollEvent())
            {
                case MftEvent.None:
                    return;
                case MftEvent.NeedInput:
                    _inputRequests++;
                    break;
                case MftEvent.HaveOutput:
                    _outputsReady++;
                    break;
            }
        }
    }
}
