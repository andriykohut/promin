using System.Net;
using System.Net.Sockets;
using Promin.Protocol.Discovery;

namespace Promin.Protocol.Tests.Discovery;

/// <summary>
/// The beacon announces once per live network interface, each time from a socket bound to that
/// interface's own address, because a single send to the limited broadcast address leaves by one
/// interface only. The list of addresses comes through a seam, so these tests supply loopback
/// addresses (127.0.0.1, 127.0.0.2 ...) and nothing here touches a real network: a datagram's
/// source address tells the receiver which interface it was sent from.
/// </summary>
public class LanDiscoveryBeaconInterfacesTests
{
    private const string DeviceId = DiscoveryAnnouncementTests.ValidId;
    private const string Thumbprint = DiscoveryAnnouncementTests.ValidThumbprint;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static readonly IPAddress Second = IPAddress.Parse("127.0.0.2");
    private static readonly IPAddress Third = IPAddress.Parse("127.0.0.3");

    /// <summary>A stand-in for the machine's network adapters, with every address on loopback.</summary>
    private sealed class FakeInterfaces(params IPAddress[] initial) : IAnnouncementInterfaces
    {
        private readonly Lock _gate = new();
        private List<IPAddress> _addresses = [.. initial];

        public List<IPAddress> BindAttempts { get; } = [];
        public Dictionary<IPAddress, UdpClient> Bound { get; } = [];

        /// <summary>Addresses whose bind throws, as an adapter that has just gone away does.</summary>
        public HashSet<IPAddress> FailToBind { get; } = [];

        /// <summary>Addresses whose bind throws something other than a socket error (a bug in the seam, say).</summary>
        public HashSet<IPAddress> FailToBindUnexpectedly { get; } = [];

        /// <summary>Addresses whose socket is dead on arrival, so every send from it throws.</summary>
        public HashSet<IPAddress> FailToSend { get; } = [];

        public void Set(params IPAddress[] addresses)
        {
            lock (_gate)
            {
                _addresses = [.. addresses];
            }
        }

        public IReadOnlyList<IPAddress> GetLocalAddresses()
        {
            lock (_gate)
            {
                return [.. _addresses];
            }
        }

        public UdpClient Bind(IPAddress local)
        {
            lock (_gate)
            {
                BindAttempts.Add(local);
            }

            if (FailToBind.Contains(local))
            {
                throw new SocketException((int)SocketError.AddressNotAvailable);
            }

            if (FailToBindUnexpectedly.Contains(local))
            {
                throw new InvalidOperationException("the seam failed in a way nobody planned for");
            }

            var client = new UdpClient(new IPEndPoint(local, 0));
            if (FailToSend.Contains(local))
            {
                client.Dispose();
            }

            lock (_gate)
            {
                Bound[local] = client;
            }

            return client;
        }

        public bool IsClosed(IPAddress local)
        {
            UdpClient client;
            lock (_gate)
            {
                client = Bound[local];
            }

            // A disposed UdpClient lets go of its socket (Client becomes null).
            return client.Client is null;
        }
    }

    /// <summary>A receiver on loopback that records the source address of every datagram.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly Lock _gate = new();
        private readonly HashSet<IPAddress> _sources = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Listener()
        {
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var received = await _socket.ReceiveAsync(_stop.Token);
                        if (DiscoveryAnnouncement.TryDecode(received.Buffer, out _))
                        {
                            lock (_gate)
                            {
                                _sources.Add(received.RemoteEndPoint.Address);
                            }
                        }
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                }
            });
        }

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

        public bool HasHeardFrom(IPAddress source)
        {
            lock (_gate)
            {
                return _sources.Contains(source);
            }
        }

        public void ForgetEverything()
        {
            lock (_gate)
            {
                _sources.Clear();
            }
        }

        public async Task WaitToHearFromAsync(params IPAddress[] sources)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!sources.All(HasHeardFrom))
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"never heard from {string.Join(", ", sources.Where(s => !HasHeardFrom(s)))}");
                }

                await Task.Delay(10);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }

    private static LanDiscoveryBeacon BeaconOn(Listener listener, FakeInterfaces interfaces) =>
        new(DeviceId, "PC-04", 17843, Thumbprint, listener.Port, intervalMs: 5, interfaces, IPAddress.Loopback);

    [Fact]
    public async Task Beacon_SendsFromEveryAddressItsSeamSupplies()
    {
        using var listener = new Listener();
        var interfaces = new FakeInterfaces(IPAddress.Loopback, Second, Third);
        using var beacon = BeaconOn(listener, interfaces);

        beacon.Start();

        await listener.WaitToHearFromAsync(IPAddress.Loopback, Second, Third);
    }

    [Fact]
    public async Task Beacon_AnAddressThatFailsToBind_DoesNotStopTheOthers()
    {
        using var listener = new Listener();
        var unbindable = IPAddress.Parse("192.0.2.77");
        var interfaces = new FakeInterfaces(unbindable, IPAddress.Loopback, Second);
        interfaces.FailToBind.Add(unbindable);
        using var beacon = BeaconOn(listener, interfaces);

        beacon.Start();

        await listener.WaitToHearFromAsync(IPAddress.Loopback, Second);
        Assert.Contains(unbindable, interfaces.BindAttempts);
        Assert.False(listener.HasHeardFrom(unbindable));
    }

    /// <summary>
    /// "A failure on one interface does not stop the others" has to hold for whatever the seam
    /// throws, or one bad interface ends the loop silently and the PC stops announcing anywhere.
    /// </summary>
    [Fact]
    public async Task Beacon_AnAddressWhoseBindThrowsSomethingUnexpected_DoesNotStopTheOthers()
    {
        using var listener = new Listener();
        var unexpected = IPAddress.Parse("192.0.2.78");
        var interfaces = new FakeInterfaces(unexpected, IPAddress.Loopback, Second);
        interfaces.FailToBindUnexpectedly.Add(unexpected);
        using var beacon = BeaconOn(listener, interfaces);

        beacon.Start();

        await listener.WaitToHearFromAsync(IPAddress.Loopback, Second);
    }

    [Fact]
    public async Task Beacon_AnAddressThatFailsToSend_DoesNotStopTheOthers()
    {
        using var listener = new Listener();
        var interfaces = new FakeInterfaces(Second, IPAddress.Loopback, Third);
        interfaces.FailToSend.Add(Second);
        using var beacon = BeaconOn(listener, interfaces);

        beacon.Start();

        // Second's socket throws on every send; it is tried before the other two on every round.
        await listener.WaitToHearFromAsync(IPAddress.Loopback, Third);
        Assert.False(listener.HasHeardFrom(Second));
    }

    [Fact]
    public async Task Beacon_PicksUpAChangedList_AndLetsGoOfAnAddressThatLeft()
    {
        using var listener = new Listener();
        var interfaces = new FakeInterfaces(IPAddress.Loopback);
        using var beacon = BeaconOn(listener, interfaces);
        beacon.Start();
        await listener.WaitToHearFromAsync(IPAddress.Loopback);
        Assert.False(listener.HasHeardFrom(Second));

        // A PC that joins Wi-Fi after the beacon started must begin announcing there.
        interfaces.Set(IPAddress.Loopback, Second);
        await listener.WaitToHearFromAsync(Second);

        // And one that leaves the network stops being used, and its socket is closed.
        interfaces.Set(IPAddress.Loopback);
        var deadline = DateTime.UtcNow + Patience;
        while (!interfaces.IsClosed(Second))
        {
            Assert.True(DateTime.UtcNow < deadline, "the socket of an address that left was never closed");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        listener.ForgetEverything();
        await listener.WaitToHearFromAsync(IPAddress.Loopback);
        Assert.False(listener.HasHeardFrom(Second));
    }

    [Fact]
    public async Task Beacon_RetriesAnAddressWhoseBindFailed_AtTheNextRefresh()
    {
        using var listener = new Listener();
        var interfaces = new FakeInterfaces(IPAddress.Loopback, Second);
        interfaces.FailToBind.Add(Second);
        using var beacon = BeaconOn(listener, interfaces);
        beacon.Start();
        await listener.WaitToHearFromAsync(IPAddress.Loopback);

        interfaces.FailToBind.Remove(Second);      // the adapter has finished coming up

        await listener.WaitToHearFromAsync(Second);
    }

    [Fact]
    public async Task Beacon_ClosesEverySocketItOpened_WhenDisposed()
    {
        using var listener = new Listener();
        var interfaces = new FakeInterfaces(IPAddress.Loopback, Second);
        var beacon = BeaconOn(listener, interfaces);
        beacon.Start();
        await listener.WaitToHearFromAsync(IPAddress.Loopback, Second);

        beacon.Dispose();

        Assert.True(interfaces.IsClosed(IPAddress.Loopback));
        Assert.True(interfaces.IsClosed(Second));
    }

    [Fact]
    public async Task Beacon_KeepsRunning_WhenTheSeamItselfThrows()
    {
        using var listener = new Listener();
        var interfaces = new ThrowingInterfaces(IPAddress.Loopback);
        using var beacon = new LanDiscoveryBeacon(
            DeviceId, "PC-04", 17843, Thumbprint, listener.Port, 5, interfaces, IPAddress.Loopback);
        beacon.Start();
        await listener.WaitToHearFromAsync(IPAddress.Loopback);

        interfaces.Throw = true;      // enumerating adapters fails from now on
        listener.ForgetEverything();

        // It carries on with the interfaces it already knew.
        await listener.WaitToHearFromAsync(IPAddress.Loopback);
    }

    private sealed class ThrowingInterfaces(IPAddress address) : IAnnouncementInterfaces
    {
        public volatile bool Throw;

        public IReadOnlyList<IPAddress> GetLocalAddresses() =>
            Throw ? throw new System.Net.NetworkInformation.NetworkInformationException() : [address];

        public UdpClient Bind(IPAddress local) => new(new IPEndPoint(local, 0));
    }

    [Fact]
    public void TheSystemSeam_ReportsOnlyUsableIpv4AddressesOfInterfacesThatAreUp()
    {
        // Reads the machine's adapter list; sends and binds nothing.
        var addresses = new SystemAnnouncementInterfaces().GetLocalAddresses();

        Assert.All(addresses, address =>
        {
            Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily);
            Assert.False(IPAddress.IsLoopback(address));
            Assert.NotEqual(IPAddress.Any, address);
        });
        Assert.Equal(addresses.Count, addresses.Distinct().Count());
    }

    [Fact]
    public void ByDefault_TheBeaconAnnouncesToTheLanBroadcastAddress_ThroughTheSystemSeam()
    {
        // Never started, so nothing is sent or bound: this only reads what production would do.
        using var beacon = new LanDiscoveryBeacon(DeviceId, "PC-04", 17843, Thumbprint, ProminPorts.Discovery);

        Assert.Equal(IPAddress.Broadcast, beacon.Destination);
        Assert.IsType<SystemAnnouncementInterfaces>(beacon.Interfaces);
    }
}
