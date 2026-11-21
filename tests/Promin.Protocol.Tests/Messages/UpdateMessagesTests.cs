using System.Buffers;
using System.Text;
using Promin.Protocol.Messages;

namespace Promin.Protocol.Tests.Messages;

/// <summary>
/// The update path stays fixed from its first release on, so that any newer console can update any agent that has it:
/// these exact bytes must keep decoding and encoding the same way.
/// </summary>
public class UpdateMessagesTests
{
    private static ControlMessage Decode(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var framed = new byte[4 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        Assert.True(ControlMessageCodec.TryDecode(new ReadOnlySequence<byte>(framed), out var message, out _));
        return message!;
    }

    [Theory]
    [InlineData("""{"$type":"askVersion"}""")]
    [InlineData("""{"$type":"agentVersion","Version":"0.4.0"}""")]
    [InlineData("""{"$type":"offerUpdate","Version":"0.4.0","Length":3,"Signature":"AQID"}""")]
    [InlineData("""{"$type":"updateAnswer","Outcome":2}""")]
    public void TheUpdatePathIsFixed(string json) =>
        Assert.Equal(json, Encoding.UTF8.GetString(ControlMessageCodec.Encode(Decode(json)).AsSpan(4)));

    [Fact]
    public void TheOutcomesKeepTheirNumbers()
    {
        Assert.Equal(0, (int)UpdateOutcome.Accepted);
        Assert.Equal(1, (int)UpdateOutcome.BadSignature);
        Assert.Equal(2, (int)UpdateOutcome.NotNewer);
        Assert.Equal(3, (int)UpdateOutcome.Failed);
    }

    [Fact]
    public void TheDecodedMessagesCarryTheirFields()
    {
        Assert.IsType<AskVersion>(Decode("""{"$type":"askVersion"}"""));
        Assert.Equal("0.4.0", Assert.IsType<AgentVersion>(Decode("""{"$type":"agentVersion","Version":"0.4.0"}""")).Version);
        var offer = Assert.IsType<OfferUpdate>(Decode("""{"$type":"offerUpdate","Version":"0.4.0","Length":3,"Signature":"AQID"}"""));
        Assert.Equal(("0.4.0", 3L), (offer.Version, offer.Length));
        Assert.Equal([1, 2, 3], offer.Signature);
        Assert.Equal(UpdateOutcome.NotNewer, Assert.IsType<UpdateAnswer>(Decode("""{"$type":"updateAnswer","Outcome":2}""")).Outcome);
    }
}
