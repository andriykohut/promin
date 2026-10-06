using Educast.Protocol.Transport;
using Educast.Protocol.Video;

namespace Educast.Protocol.Broadcast;

/// <summary>
/// The student PC's display, as the session sees it: something that turns one encoded frame into a picture on
/// screen. Implemented in <c>Educast.Agent</c> over the H.264 decoder and the render window; it owns no
/// decisions, so the session stays testable with no GPU.
/// </summary>
public interface IFramePresenter
{
    /// <summary>
    /// Decodes <paramref name="encodedFrame"/> (without its envelope) and puts the picture on screen. True when
    /// a picture was shown. False when none was: the picture on screen stays as it was. Throws only when the
    /// display itself has failed.
    /// </summary>
    bool TryDecodeAndPresent(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame);
}

/// <summary>
/// The student side of a broadcast, as logic: which of the frames that arrive are offered to the decoder, and
/// when to wait for a keyframe. Everything else (decoding, drawing, the window) is behind
/// <see cref="IFramePresenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no reassembly: QUIC delivers each frame whole, in order within its own stream. What is left is that
/// separate streams complete independently, and that a frame can be lost on the way.
/// </para>
/// <para>
/// <b>The rule.</b> A frame is offered to the decoder only if the decoder has everything before it. Frame ids rise
/// by one per frame the master produces, so the next frame after one that arrived has the id just after it. Any
/// other id means a frame between them did not arrive; nothing that depends on a missing frame can be decoded
/// correctly, so every frame is discarded until a keyframe, which depends on nothing, has been offered. The
/// picture already on screen stays (a short freeze, never a smeared decode). The same wait begins at the first
/// frame ever received and after <see cref="OnBroadcastStarted"/>. The session decides this on its own account:
/// it never asks the decoder whether it wants a keyframe. Once a keyframe has been offered, every later frame in
/// sequence is offered too, whatever the decoder returned: a decoder that holds one picture back needs the next
/// frame to give the keyframe's picture, and one that could not use a keyframe refuses what follows by itself
/// (those calls are counted as refused).
/// </para>
/// <para>
/// <b>What a gap can mean.</b> The master (<see cref="BroadcastSession"/>) stops sending deltas to a client after
/// it loses a frame for that client, so after a loss its own side causes, the next frame this session sees is a
/// keyframe, and the rule above costs nothing. A gap followed by a delta therefore means a frame was lost after
/// the master considered it sent: dropped from this PC's receive queue because the receive loop was behind,
/// aborted by the sender's own deadline part-way through, refused here for its size or shape, or (rarely)
/// finished behind its successor, in which case the gate refuses it when it does arrive. This rule is the second
/// line of defence, for exactly those. A gap cannot mean that the source had nothing new (no id is spent on
/// that), or a restarted master (its ids begin again at 0, which is why <see cref="OnBroadcastStarted"/> exists).
/// </para>
/// <para>
/// <b>Counters.</b> All are running totals for the life of the object and are not cleared by
/// <see cref="OnBroadcastStarted"/>. Nothing in the control protocol carries them to the console; they are for the
/// agent's log. <see cref="DroppedFrameCount"/>: payloads that were not frames, or frames the gate refused as
/// stale or duplicate. <see cref="DiscardedFrameCount"/>: frames in order as far as the gate is concerned, kept
/// from the decoder by the rule above. <see cref="RefusedFrameCount"/>: frames the presenter was offered and did
/// not show. <see cref="PresentedFrameCount"/>: pictures shown. A frame is in at most one of them.
/// </para>
/// <para>
/// Driven from one thread at a time (in the agent, the display thread), so it holds no locks.
/// </para>
/// </remarks>
public sealed class ClientAgentSession(IFramePresenter presenter)
{
    private readonly StaleFrameGate _gate = new();
    private uint? _lastAcceptedId;
    private bool _awaitingKeyframe = true;
    private int _oversizedFrameCount;

    /// <summary>Raised with the frame's id each time a picture has been shown.</summary>
    public event Action<uint>? FramePresented;

    /// <summary>Payloads that were not frames (too short for an envelope, or longer than a frame may be), and frames that were stale or duplicates.</summary>
    public int DroppedFrameCount => _gate.DroppedFrameCount + _oversizedFrameCount;

    /// <summary>Frames kept from the decoder because an earlier frame is missing or the decoder cannot use them yet.</summary>
    public int DiscardedFrameCount { get; private set; }

    /// <summary>Frames the presenter was offered and did not show.</summary>
    public int RefusedFrameCount { get; private set; }

    /// <summary>Pictures shown.</summary>
    public int PresentedFrameCount { get; private set; }

    /// <summary>
    /// A broadcast has started on this connection: the master's frame ids begin again at 0 and its first frame
    /// is a keyframe. Without this the gate would refuse every frame of a restarted master, since none is newer
    /// than the last frame of the old broadcast. The counters are kept.
    /// </summary>
    public void OnBroadcastStarted()
    {
        _gate.Reset();
        _lastAcceptedId = null;
        _awaitingKeyframe = true;
    }

    /// <summary>
    /// Called once per completed inbound video stream: the payload is the envelope followed by one whole encoded
    /// frame. Throws nothing for a payload that is not a frame.
    /// </summary>
    public void OnVideoFrameReceived(ReadOnlyMemory<byte> streamPayload)
    {
        if (streamPayload.Length > QuicChannel.MaxVideoFrameBytes)
        {
            _oversizedFrameCount++;
            return;
        }

        if (!_gate.TryAccept(streamPayload, out var envelope, out var encodedFrame))
        {
            return;
        }

        // The gate only passes ids newer than the last it passed, so a different id is a later one: a gap.
        if (_lastAcceptedId is { } last && (long)envelope.FrameId != (long)last + 1)
        {
            _awaitingKeyframe = true;
        }

        _lastAcceptedId = envelope.FrameId;

        if (_awaitingKeyframe && !envelope.IsKeyframe)
        {
            DiscardedFrameCount++;
            return;
        }

        // A keyframe that is offered ends the wait, whatever the decoder makes of it.
        if (envelope.IsKeyframe)
        {
            _awaitingKeyframe = false;
        }

        if (!presenter.TryDecodeAndPresent(envelope.FrameId, envelope.IsKeyframe, encodedFrame))
        {
            RefusedFrameCount++;
            return;
        }

        PresentedFrameCount++;
        FramePresented?.Invoke(envelope.FrameId);
    }
}
