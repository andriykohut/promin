using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// The tuning values are the protocol — there is no heartbeat message, so liveness is these
/// numbers and nothing else. They are pinned so that changing one is a deliberate act that has
/// to edit a test, rather than a drive-by.
/// </summary>
public class QuicTransportSettingsTests
{
    /// <summary>
    /// 25 s idle, 2 s keep-alive. One GC pause or thermal stall on the master must not drop all
    /// twenty connections at once, which a 10 s idle timeout did.
    /// </summary>
    [Fact]
    public void Liveness_IsATwentyFiveSecondIdleTimeout_WithATwoSecondKeepAlive()
    {
        Assert.Equal(TimeSpan.FromSeconds(25), QuicTransportSettings.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), QuicTransportSettings.KeepAliveInterval);
    }

    /// <summary>
    /// The agent grants 256 streams for broadcast video. The master grants 4 for screen pictures on an ordinary
    /// connection (one is in use at a time) and none on a pairing connection.
    /// </summary>
    [Fact]
    public void UnidirectionalCredit_IsVideoForTheAgent_AndFourPicturesForTheMaster_NoneWhilePairing()
    {
        Assert.Equal(256, QuicTransportSettings.AgentMaxInboundUnidirectionalStreams);
        Assert.Equal(4, QuicTransportSettings.MasterMaxInboundUnidirectionalStreams);
        Assert.Equal(0, QuicTransportSettings.MasterPairingMaxInboundUnidirectionalStreams);
    }

    /// <summary>Pictures are small: the most an agent PC can make the console buffer is 4 pictures of 1 MB.</summary>
    [Fact]
    public void InboundPictures_BufferAtMostFourMegabytesPerConnection() =>
        Assert.Equal(
            4 * 1024 * 1024,
            QuicTransportSettings.MasterMaxInboundUnidirectionalStreams * QuicChannel.MaxPictureBytes);

    /// <summary>The master opens the control stream; an agent is granted none of its own.</summary>
    [Fact]
    public void ControlStreamCredit_IsGrantedByTheAgentOnly()
    {
        Assert.Equal(0, QuicTransportSettings.MasterMaxInboundBidirectionalStreams);
        Assert.Equal(4, QuicTransportSettings.MaxInboundBidirectionalStreams);
    }

    /// <summary>The buffered worst case per connection is 8 frames of 4 MB, not 256.</summary>
    [Fact]
    public void InboundVideo_BuffersAtMostThirtyTwoMegabytesPerConnection()
    {
        Assert.Equal(8, QuicTransportSettings.MaxConcurrentInboundVideoStreams);
        Assert.Equal(
            32 * 1024 * 1024,
            QuicTransportSettings.MaxConcurrentInboundVideoStreams * QuicChannel.MaxVideoFrameBytes);
    }
}
