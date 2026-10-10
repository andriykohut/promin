using System.Net;
using System.Net.Sockets;

namespace Promin.Protocol.Discovery;

/// <summary>
/// The student PC's side of discovery: announces this device on the LAN, over and over, so a
/// teacher's console that starts listening at any time finds it within one interval.
/// </summary>
/// <remarks>
/// <b>Once per network.</b> A single send to the limited broadcast address (255.255.255.255)
/// leaves by one interface only, whichever Windows prefers for its default route, so a PC with
/// Wi-Fi and Ethernet, a VPN, or a virtual-machine adapter could announce onto the wrong network
/// and no console would ever see it. Each announcement is therefore sent once for every IPv4
/// address of every network interface that is up (and is not loopback), each time from a socket
/// bound to that address. The interfaces are looked at again every
/// <see cref="RefreshEveryAnnouncements"/> announcements, so a PC that joins Wi-Fi after the
/// beacon started begins announcing there, and one that leaves a network stops using it. A
/// failure on one interface (it cannot be bound, a send throws) is that interface's alone: the
/// others carry on, and a failed bind is tried again at the next look.
///
/// Tests pass their own interface list (see <see cref="IAnnouncementInterfaces"/>) and a
/// loopback destination, so that nothing ever goes onto a real network. The sockets are opened
/// by <see cref="Start"/>, not by the constructor.
///
/// The announcement says only where to dial: see <see cref="DiscoveryAnnouncement"/>.
/// </remarks>
public sealed class LanDiscoveryBeacon : IDisposable
{
    /// <summary>How many announcements go out between two looks at the network interfaces.</summary>
    internal const int RefreshEveryAnnouncements = 10;

    private readonly byte[] _datagram;
    private readonly int _discoveryPort;
    private readonly int _intervalMs;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    /// <param name="deviceId">The install's device id (32 lowercase hex characters).</param>
    /// <param name="displayName">What a person reads; the machine name on a real agent.</param>
    /// <param name="quicPort">Where this device accepts QUIC connections.</param>
    /// <param name="certificateThumbprint">The device certificate's thumbprint, 40 or 64 hex characters.</param>
    /// <param name="discoveryPort">The UDP port the console listens on.</param>
    /// <param name="intervalMs">How often to announce.</param>
    /// <exception cref="ArgumentException">
    /// The announcement would be refused by every scanner (a malformed id, port or thumbprint),
    /// which is a bug in the caller rather than something to discover on the network later.
    /// </exception>
    public LanDiscoveryBeacon(
        string deviceId,
        string displayName,
        int quicPort,
        string certificateThumbprint,
        int discoveryPort,
        int intervalMs = 2000)
        : this(
            deviceId,
            displayName,
            quicPort,
            certificateThumbprint,
            discoveryPort,
            intervalMs,
            new SystemAnnouncementInterfaces(),
            IPAddress.Broadcast)
    {
    }

    /// <summary>The same, with the network interfaces and the destination supplied (by tests).</summary>
    internal LanDiscoveryBeacon(
        string deviceId,
        string displayName,
        int quicPort,
        string certificateThumbprint,
        int discoveryPort,
        int intervalMs,
        IAnnouncementInterfaces interfaces,
        IPAddress destination)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(discoveryPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(discoveryPort, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMs, 1);

        _datagram = new DiscoveryAnnouncement(deviceId, displayName, quicPort, certificateThumbprint).Encode();
        if (!DiscoveryAnnouncement.TryDecode(_datagram, out _))
        {
            throw new ArgumentException(
                "The device id, port or certificate thumbprint is not one a scanner would accept.");
        }

        _discoveryPort = discoveryPort;
        _intervalMs = intervalMs;
        Interfaces = interfaces;
        Destination = destination;
    }

    /// <summary>The address announcements are sent to.</summary>
    internal IPAddress Destination { get; }

    /// <summary>Where the addresses to announce from come from.</summary>
    internal IAnnouncementInterfaces Interfaces { get; }

    /// <summary>Begins announcing: once straight away, then every interval, until disposed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The beacon has already been started.");
            }

            _started = true;
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var destination = new IPEndPoint(Destination, _discoveryPort);
        var sockets = new Dictionary<IPAddress, UdpClient>();
        try
        {
            for (var round = 0; !ct.IsCancellationRequested; round++)
            {
                if (round % RefreshEveryAnnouncements == 0)
                {
                    Refresh(sockets);
                }

                foreach (var (_, socket) in sockets.ToArray())
                {
                    try
                    {
                        await socket.SendAsync(_datagram, destination, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception e) when (e is SocketException or ObjectDisposedException)
                    {
                        // This interface cannot send right now (its cable is out, its adapter is
                        // resetting). The others still go out, and the next look at the network
                        // drops it if it has gone for good. (This beacon's own disposal arrives as
                        // the cancellation above, never as a disposed socket.)
                    }
                }

                try
                {
                    await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            foreach (var socket in sockets.Values)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// Brings the open sockets in line with the interfaces there are now: closes those of
    /// addresses that have gone, opens one for each address that is new or whose last bind
    /// failed. Whatever the seam throws, this does not: when the interfaces cannot be listed
    /// the ones in use stay in use, and an address that cannot be bound is skipped and tried
    /// again at the next listing. One bad interface never ends the beacon loop.
    /// </summary>
    private void Refresh(Dictionary<IPAddress, UdpClient> sockets)
    {
        IReadOnlyList<IPAddress>? current;
        try
        {
            current = Interfaces.GetLocalAddresses();
        }
        catch (Exception)
        {
            return;
        }

        if (current is null)
        {
            return;
        }

        foreach (var gone in sockets.Keys.Where(address => !current.Contains(address)).ToArray())
        {
            if (sockets.Remove(gone, out var socket))
            {
                socket.Dispose();
            }
        }

        foreach (var address in current.Where(address => address is not null && !sockets.ContainsKey(address)).ToArray())
        {
            try
            {
                sockets[address] = Interfaces.Bind(address);
            }
            catch (Exception)
            {
                // This interface only: tried again at the next listing.
            }
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            loop = _loop;
        }

        try
        {
            // The loop closes its own sockets on the way out.
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop only ever ends by cancellation.
        }

        _cts.Dispose();
    }
}
