using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Educast.Protocol.Discovery;

/// <summary>
/// The beacon's view of the machine's network adapters, behind a seam so tests can supply
/// loopback addresses and never touch the real network.
/// </summary>
internal interface IAnnouncementInterfaces
{
    /// <summary>The local IPv4 addresses to announce from, one per address to use.</summary>
    IReadOnlyList<IPAddress> GetLocalAddresses();

    /// <summary>Opens a socket bound to <paramref name="local"/> (any port), able to send broadcasts.</summary>
    /// <exception cref="SocketException">The address cannot be bound, for instance because the adapter has just gone.</exception>
    UdpClient Bind(IPAddress local);
}

/// <summary>
/// The real thing: every IPv4 address of every network interface that is up and is not
/// loopback. A broadcast sent from a socket bound to one of these addresses leaves by that
/// interface, which is what makes the announcement reach each network the PC is on and not just
/// the one Windows prefers for its default route.
/// </summary>
internal sealed class SystemAnnouncementInterfaces : IAnnouncementInterfaces
{
    public IReadOnlyList<IPAddress> GetLocalAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address)
                    && !address.Equals(IPAddress.Any)
                    && !addresses.Contains(address))
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }

    public UdpClient Bind(IPAddress local) =>
        new(new IPEndPoint(local, 0)) { EnableBroadcast = true };
}
