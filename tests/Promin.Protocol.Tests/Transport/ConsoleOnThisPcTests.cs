using System.Net;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

public class ConsoleOnThisPcTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("192.168.1.21", false)]
    public void Is_ComparesWithThisPcsAddresses(string remote, bool expected) =>
        Assert.Equal(expected, ConsoleOnThisPc.Is(IPAddress.Parse(remote), [IPAddress.Parse("192.168.1.20")]));
}
