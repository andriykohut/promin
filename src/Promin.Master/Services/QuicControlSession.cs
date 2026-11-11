using Promin.Master.Core.Control;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;
using Promin.Protocol.Video;

namespace Promin.Master.Services;

/// <summary>
/// Control of one agent PC over its own bidirectional stream; its video arrives on the connection's shared video
/// streams, as <paramref name="frames"/>.
/// </summary>
internal sealed class QuicControlSession(QuicControlChannel stream, Func<CancellationToken, ValueTask<byte[]>> frames) : IControlSession
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _started;

    /// <summary>
    /// Frames left over from an earlier session are dropped: the first frame returned is a keyframe, and every
    /// frame after it is returned.
    /// </summary>
    public async ValueTask<byte[]> ReceiveFrameAsync(CancellationToken ct)
    {
        while (true)
        {
            var frame = await frames(ct).ConfigureAwait(false);
            if (_started || (frame.Length >= VideoFrameEnvelope.Size && VideoFrameEnvelope.ReadFrom(frame).IsKeyframe))
            {
                _started = true;
                return frame;
            }
        }
    }

    public Task<ControlMessage> ReceiveAsync(CancellationToken ct) => stream.ReceiveAsync(ct);

    public async Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.SendAsync(message, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
