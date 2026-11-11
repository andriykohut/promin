using System.Threading.Channels;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

public sealed class AgentStreamDemuxTests
{
    private readonly Channel<byte[]> _streams = Channel.CreateUnbounded<byte[]>();

    private AgentStreamDemux Demux() => new(ct => _streams.Reader.ReadAsync(ct));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APicture_GoesToPictures_WithoutItsKind()
    {
        var demux = Demux();
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.Picture, [7, 8]));

        Assert.Equal(new byte[] { 7, 8 }, await demux.ReceivePictureAsync(Ct));
    }

    [Fact]
    public async Task AControlFrame_GoesToControlFrames_WithoutItsKind()
    {
        var demux = Demux();
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.ControlVideo, [9]));

        Assert.Equal(new byte[] { 9 }, await demux.ReceiveControlFrameAsync(Ct));
    }

    [Fact]
    public async Task AnEmptyPicture_IsDeliveredEmpty()
    {
        var demux = Demux();
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.Picture, []));

        Assert.Empty(await demux.ReceivePictureAsync(Ct));
    }

    [Fact]
    public async Task AnUnknownKind_OrAnEmptyStream_IsDropped()
    {
        var demux = Demux();
        _streams.Writer.TryWrite([99, 1]);
        _streams.Writer.TryWrite([]);
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.Picture, [5]));
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.ControlVideo, [6]));

        Assert.Equal(new byte[] { 5 }, await demux.ReceivePictureAsync(Ct));
        Assert.Equal(new byte[] { 6 }, await demux.ReceiveControlFrameAsync(Ct));
    }

    [Fact]
    public async Task PicturesKeepOnlyTheNewest()
    {
        var demux = Demux();
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.Picture, [1]));
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.Picture, [2]));
        _streams.Writer.TryWrite(AgentStream.Tag(AgentStream.ControlVideo, [3]));

        // The control frame behind both pictures is delivered only after the pump has taken the pictures.
        await demux.ReceiveControlFrameAsync(Ct);

        Assert.Equal(new byte[] { 2 }, await demux.ReceivePictureAsync(Ct));
    }

    [Fact]
    public async Task TheEndOfTheConnection_EndsBothReaders()
    {
        var ended = new IOException("gone");
        var demux = new AgentStreamDemux(_ => throw ended);

        Assert.Same(ended, (await Assert.ThrowsAsync<ChannelClosedException>(async () => await demux.ReceivePictureAsync(Ct))).InnerException);
        Assert.Same(ended, (await Assert.ThrowsAsync<ChannelClosedException>(async () => await demux.ReceiveControlFrameAsync(Ct))).InnerException);
    }
}
