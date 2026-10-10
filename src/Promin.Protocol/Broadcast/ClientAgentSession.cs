using Promin.Protocol.Transport;
using Promin.Protocol.Video;

namespace Promin.Protocol.Broadcast;

/// <summary>
/// The agent PC's display, as the session sees it: something that decodes one encoded frame and then shows the
/// picture. Implemented in <c>Promin.Agent</c> over the H.264 decoder and the render window; it owns no
/// decisions, so the session stays testable with no GPU.
/// </summary>
public interface IFramePresenter
{
    /// <summary>
    /// Decodes <paramref name="encodedFrame"/> (without its envelope). True when a picture came out of this call.
    /// False when none did: nothing is shown for it and the picture of any earlier call is gone. Throws only when
    /// the display itself has failed.
    /// </summary>
    bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame);

    /// <summary>
    /// Shows the picture that the most recent <see cref="TryDecode"/> gave, which returned true. Called at most
    /// once per decode and always before the next decode.
    /// </summary>
    void PresentDecoded();
}

/// <summary>
/// The agent side of a broadcast, as logic: which of the frames that arrive reach the decoder, which picture is
/// shown, and when to wait for a keyframe. Everything else (decoding, drawing, the window) is behind
/// <see cref="IFramePresenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no reassembly: QUIC delivers each frame whole, in order within its own stream. What is left is that
/// separate streams complete independently, and that a frame can be lost on the way.
/// </para>
/// <para>
/// <b>The rule.</b> A frame is handed to the decoder only if the decoder has everything before it. Frame ids rise
/// by one per frame the master produces, so the next frame after one that arrived has the id just after it. Any
/// other id means a frame between them did not arrive; nothing that depends on a missing frame can be decoded
/// correctly, so every frame is discarded until a keyframe, which depends on nothing, has been handed over. The
/// picture already on screen stays (a short freeze, never a smeared decode). The same wait begins at the first
/// frame ever received and after <see cref="OnBroadcastStarted"/>. The session decides this on its own account:
/// it never asks the decoder whether it wants a keyframe. Once a keyframe has been handed over, every later frame
/// in sequence is handed over too, whatever the decoder returned: a decoder that holds one picture back needs
/// the next frame to give the keyframe's picture, and one that could not use a keyframe refuses what follows by
/// itself (those calls are counted as refused).
/// </para>
/// <para>
/// <b>What a gap can mean.</b> The master (<see cref="BroadcastSession"/>) stops sending deltas to a client after
/// it loses a frame for that client, so after a loss its own side causes, the next frame this session sees is a
/// keyframe, and the rule costs nothing. A gap followed by a delta therefore means a frame was lost after the
/// master considered it sent: dropped from this PC's receive queue because the display was behind (see
/// <see cref="QuicChannel"/>'s receive queue, which is where an overloaded agent PC loses frames, on purpose),
/// aborted by the sender's own deadline part-way through, refused here for its size or shape, or (rarely)
/// finished behind its successor, in which case the gate refuses it when it does arrive. A gap cannot mean that
/// the source had nothing new (no id is spent on that), or a restarted master (its ids begin again at 0, which
/// is why <see cref="OnBroadcastStarted"/> exists).
/// </para>
/// <para>
/// <b>Staying live.</b> When the caller says more frames are already waiting (<c>moreWaiting</c>), a frame is
/// still decoded, in order, because a delta needs the one before it, but its picture is not shown: only the last
/// picture of the batch is. An agent PC that never catches up would then show almost nothing, so no more than
/// two decoded pictures in a row go unshown: the picture after two such skips is shown even though more frames
/// are waiting (under permanent backlog, every third decoded picture). A decoded picture that was replaced or
/// lost before it could be shown is counted in <see cref="SkippedPictureCount"/>.
/// </para>
/// <para>
/// <b>Counters.</b> All are running totals for the life of the object and are not cleared by
/// <see cref="OnBroadcastStarted"/>. Nothing in the control protocol carries them to the console; they are for the
/// agent's log. <see cref="DroppedFrameCount"/>: payloads that were not frames, or frames the gate refused as
/// stale or duplicate. <see cref="DiscardedFrameCount"/>: frames in order as far as the gate is concerned, kept
/// from the decoder by the rule above. <see cref="RefusedFrameCount"/>: frames handed to the decoder that gave no
/// picture. <see cref="SkippedPictureCount"/>: pictures decoded and never shown, to stay current.
/// <see cref="PresentedFrameCount"/>: pictures shown. A frame is in at most one of the first three, and a
/// decoded frame is in exactly one of the last two or still waiting to be shown.
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

    // Most decoded pictures that may go unshown in a row while more frames are waiting.
    private const int MaxConsecutiveSkippedPictures = 2;

    // Decoded pictures replaced unshown since a picture was last shown (or the broadcast started).
    private int _consecutiveSkippedPictures;

    // The id of a picture that has been decoded and not yet shown.
    private uint? _pendingPictureId;

    /// <summary>Raised with the frame's id each time a picture has been shown.</summary>
    public event Action<uint>? FramePresented;

    /// <summary>Payloads that were not frames (too short for an envelope, or longer than a frame may be), and frames that were stale or duplicates.</summary>
    public int DroppedFrameCount => _gate.DroppedFrameCount + _oversizedFrameCount;

    /// <summary>Frames kept from the decoder because an earlier frame is missing, or no keyframe has been handed over yet.</summary>
    public int DiscardedFrameCount { get; private set; }

    /// <summary>Frames handed to the decoder that gave no picture.</summary>
    public int RefusedFrameCount { get; private set; }

    /// <summary>Pictures decoded and not shown, because a newer one was waiting (at most two in a row) or the decoder lost them.</summary>
    public int SkippedPictureCount { get; private set; }

    /// <summary>Pictures shown.</summary>
    public int PresentedFrameCount { get; private set; }

    /// <summary>
    /// A broadcast has started on this connection: the master's frame ids begin again at 0 and its first frame
    /// is a keyframe. Without this the gate would refuse every frame of a restarted master, since none is newer
    /// than the last frame of the old broadcast. A picture decoded and not yet shown is given up. The counters
    /// are kept.
    /// </summary>
    public void OnBroadcastStarted()
    {
        _gate.Reset();
        _lastAcceptedId = null;
        _awaitingKeyframe = true;
        GiveUpPendingPicture();
        _consecutiveSkippedPictures = 0;
    }

    /// <summary>
    /// Called once per completed inbound video stream: the payload is the envelope followed by one whole encoded
    /// frame. Throws nothing for a payload that is not a frame.
    /// </summary>
    /// <param name="moreWaiting">
    /// True when another frame is already waiting to be given to this method straight after this one. The
    /// picture of this frame is then not shown unless nothing newer is decoded, or two pictures in a row have
    /// already gone unshown.
    /// </param>
    public void OnVideoFrameReceived(ReadOnlyMemory<byte> streamPayload, bool moreWaiting = false)
    {
        Handle(streamPayload);
        if (!moreWaiting || _consecutiveSkippedPictures >= MaxConsecutiveSkippedPictures)
        {
            PresentPendingPicture();
        }
    }

    private void Handle(ReadOnlyMemory<byte> streamPayload)
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

        // A keyframe that is handed over ends the wait, whatever the decoder makes of it.
        if (envelope.IsKeyframe)
        {
            _awaitingKeyframe = false;
        }

        if (!presenter.TryDecode(envelope.FrameId, envelope.IsKeyframe, encodedFrame))
        {
            RefusedFrameCount++;
            GiveUpPendingPicture(); // a decode that gave nothing leaves nothing of the earlier picture to show
            return;
        }

        if (_pendingPictureId is not null)
        {
            SkippedPictureCount++; // replaced before it was shown
            _consecutiveSkippedPictures++;
        }

        _pendingPictureId = envelope.FrameId;
    }

    private void PresentPendingPicture()
    {
        if (_pendingPictureId is not { } frameId)
        {
            return;
        }

        _pendingPictureId = null;
        _consecutiveSkippedPictures = 0;
        presenter.PresentDecoded();
        PresentedFrameCount++;
        FramePresented?.Invoke(frameId);
    }

    private void GiveUpPendingPicture()
    {
        if (_pendingPictureId is not null)
        {
            SkippedPictureCount++;
            _pendingPictureId = null;
        }
    }
}
