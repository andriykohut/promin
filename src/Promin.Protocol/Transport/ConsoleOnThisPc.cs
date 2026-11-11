using System.Net;
using System.Net.NetworkInformation;

namespace Promin.Protocol.Transport;

/// <summary>Tells whether a console PC is this very PC, which must never be able to lock it.</summary>
public static class ConsoleOnThisPc
{
    /// <summary>True when <paramref name="remote"/> is loopback or one of <paramref name="own"/>, an IPv4-mapped address counting as its IPv4 form.</summary>
    public static bool Is(IPAddress remote, IEnumerable<IPAddress> own)
    {
        var address = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        return IPAddress.IsLoopback(address) || own.Any(mine => (mine.IsIPv4MappedToIPv6 ? mine.MapToIPv4() : mine).Equals(address));
    }

    /// <summary>The unicast addresses of every network interface of this PC.</summary>
    public static IReadOnlyList<IPAddress> OwnAddresses() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address),
    ];
}
