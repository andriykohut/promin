using System.Threading.Channels;
using Promin.Master.Core.Control;
using Promin.Protocol.Messages;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class FakeControlSession : IControlSession
{
    private readonly Channel<ControlMessage> _messages = Channel.CreateUnbounded<ControlMessage>();
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();

    public List<ControlMessage> Sent { get; } = [];

    public bool Disposed { get; private set; }

    public void Feed(ControlMessage message) => _messages.Writer.TryWrite(message);

    public void FeedFrame(byte[] frame) => _frames.Writer.TryWrite(frame);

    /// <summary>Ends the session as a lost connection does: both readers throw.</summary>
    public void Lose()
    {
        var lost = new IOException("connection lost");
        _messages.Writer.TryComplete(lost);
        _frames.Writer.TryComplete(lost);
    }

    public ValueTask<byte[]> ReceiveFrameAsync(CancellationToken ct) => _frames.Reader.ReadAsync(ct);

    public Task<ControlMessage> ReceiveAsync(CancellationToken ct) => _messages.Reader.ReadAsync(ct).AsTask();

    public Task SendAsync(ControlMessage message, CancellationToken ct)
    {
        lock (Sent)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
