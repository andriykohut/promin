using System.Threading.Channels;

namespace Promin.Protocol.Transport;

/// <summary>
/// Splits the agent PC's tagged streams (see <see cref="AgentStream"/>) into pictures and control frames. A pump
/// starts on the first read. Only the newest picture is kept; control frames queue up to eight, and a frame lost
/// there shows as a gap in the frame ids. A stream with no bytes or an unknown kind is dropped. When
/// <paramref name="receive"/> throws, both readers throw the same exception.
/// </summary>
public sealed class AgentStreamDemux(Func<CancellationToken, ValueTask<byte[]>> receive)
{
    private readonly Channel<byte[]> _pictures = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<byte[]> _controlFrames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private Task? _pump;

    /// <summary>The next picture's bytes, without the kind byte. Throws when the connection has ended.</summary>
    public ValueTask<byte[]> ReceivePictureAsync(CancellationToken ct)
    {
        StartPump();
        return _pictures.Reader.ReadAsync(ct);
    }

    /// <summary>The next control frame's bytes, without the kind byte. Throws when the connection has ended.</summary>
    public ValueTask<byte[]> ReceiveControlFrameAsync(CancellationToken ct)
    {
        StartPump();
        return _controlFrames.Reader.ReadAsync(ct);
    }

    private void StartPump() => LazyInitializer.EnsureInitialized(ref _pump, () => Task.Run(PumpAsync));

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var stream = await receive(CancellationToken.None).ConfigureAwait(false);
                if (stream.Length == 0)
                {
                    continue;
                }

                var payload = stream.AsSpan(1).ToArray();
                switch (stream[0])
                {
                    case AgentStream.Picture:
                        _pictures.Writer.TryWrite(payload);
                        break;
                    case AgentStream.ControlVideo:
                        _controlFrames.Writer.TryWrite(payload);
                        break;
                }
            }
        }
        catch (Exception ended)
        {
            _pictures.Writer.TryComplete(ended);
            _controlFrames.Writer.TryComplete(ended);
        }
    }
}
