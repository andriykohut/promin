using System.Net;
using System.Net.Sockets;
using System.Text;
using Educast.Protocol.Discovery;

namespace Educast.Protocol.Tests.Discovery;

/// <summary>
/// Beacon and scanner over loopback only. Every socket here is bound to 127.0.0.1 on a port the
/// OS chose, so a test run never puts a datagram on the real network, never collides with a
/// running Educast, and never binds a wildcard address (which is what raises a Windows Firewall
/// prompt).
/// </summary>
public class LanDiscoveryLoopbackTests
{
    private const string DeviceId = DiscoveryAnnouncementTests.ValidId;
    private const string Thumbprint = DiscoveryAnnouncementTests.ValidThumbprint;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>The adapters of a machine whose only network is loopback.</summary>
    private sealed class LoopbackOnly : IAnnouncementInterfaces
    {
        public IReadOnlyList<IPAddress> GetLocalAddresses() => [IPAddress.Loopback];

        public UdpClient Bind(IPAddress local) => new(new IPEndPoint(local, 0));
    }

    private static LanDiscoveryBeacon BeaconAt(int port, int intervalMs = 50) =>
        new(DeviceId, "PC-04", quicPort: 17843, Thumbprint, port, intervalMs, new LoopbackOnly(), IPAddress.Loopback);

    private static LanDiscoveryBeacon BeaconTo(LanDiscoveryScanner scanner, int intervalMs = 50) =>
        BeaconAt(scanner.LocalPort, intervalMs);

    private static void SendRaw(int port, byte[] datagram)
    {
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Send(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Scanner_DiscoversBeacon_WithinAShortWindow()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        using var beacon = BeaconTo(scanner);

        var discovered = new TaskCompletionSource<(DiscoveryAnnouncement Announcement, IPAddress From)>();
        scanner.DeviceDiscovered += (announcement, from) => discovered.TrySetResult((announcement, from));

        scanner.Start();
        beacon.Start();
        var (announcement, from) = await discovered.Task.WaitAsync(Patience);

        Assert.Equal(new DiscoveryAnnouncement(DeviceId, "PC-04", 17843, Thumbprint), announcement);
        Assert.Equal(IPAddress.Loopback, from);
    }

    [Fact]
    public async Task Beacon_KeepsAnnouncing_SoALateScannerStillFindsIt()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        using var beacon = BeaconTo(scanner);
        var count = 0;
        var three = new TaskCompletionSource();
        scanner.DeviceDiscovered += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 3)
            {
                three.TrySetResult();
            }
        };

        beacon.Start();
        await Task.Delay(150);          // the first announcements go to a socket nobody is reading yet
        scanner.Start();

        await three.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task Scanner_IgnoresHostileDatagrams_AndStillReportsAGoodOne()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        var seen = new List<DiscoveryAnnouncement>();
        var good = new TaskCompletionSource();
        scanner.DeviceDiscovered += (announcement, _) =>
        {
            lock (seen)
            {
                seen.Add(announcement);
            }

            good.TrySetResult();
        };
        scanner.Start();

        SendRaw(scanner.LocalPort, []);
        SendRaw(scanner.LocalPort, [1, 2, 3]);
        SendRaw(scanner.LocalPort, Encoding.UTF8.GetBytes("{\"Magic\":\"EDUCAST-ANNOUNCE-V1\"}"));
        SendRaw(scanner.LocalPort, new byte[4000]);
        SendRaw(scanner.LocalPort, Encoding.UTF8.GetBytes(new string('[', 3000)));
        SendRaw(scanner.LocalPort,
            new DiscoveryAnnouncement("agent-1", "PC-04", 17843, Thumbprint).Encode());
        SendRaw(scanner.LocalPort,
            new DiscoveryAnnouncement(DeviceId, "PC-04", 0, Thumbprint).Encode());
        SendRaw(scanner.LocalPort,
            new DiscoveryAnnouncement(DeviceId, "PC-04", 17843, "not-a-thumbprint").Encode());
        SendRaw(scanner.LocalPort,
            new DiscoveryAnnouncement(DeviceId, "PC-04", 17843, Thumbprint).Encode());   // the one good one
        await good.Task.WaitAsync(Patience);

        // Datagrams from one socket to one socket arrive in order on loopback, so everything
        // sent before the good one has been looked at by now.
        lock (seen)
        {
            var only = Assert.Single(seen);
            Assert.Equal(DeviceId, only.DeviceId);
        }
    }

    [Fact]
    public async Task Scanner_KeepsListening_WhenASubscriberThrows()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        var calls = 0;
        var second = new TaskCompletionSource();
        scanner.DeviceDiscovered += (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("subscriber bug");
            }

            second.TrySetResult();
        };
        scanner.Start();

        var datagram = new DiscoveryAnnouncement(DeviceId, "PC-04", 17843, Thumbprint).Encode();
        SendRaw(scanner.LocalPort, datagram);
        SendRaw(scanner.LocalPort, datagram);

        await second.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task Scanner_CleansTheDisplayNameItReports()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        var discovered = new TaskCompletionSource<DiscoveryAnnouncement>();
        scanner.DeviceDiscovered += (announcement, _) => discovered.TrySetResult(announcement);
        scanner.Start();

        SendRaw(scanner.LocalPort, Encoding.UTF8.GetBytes(
            "{\"Magic\":\"EDUCAST-ANNOUNCE-V1\",\"DeviceId\":\"" + DeviceId + "\"," +
            "\"DisplayName\":\"  PC-04\\u0007\\n \",\"QuicPort\":17843,\"CertificateThumbprint\":\"" + Thumbprint + "\"}"));

        Assert.Equal("PC-04", (await discovered.Task.WaitAsync(Patience)).DisplayName);
    }

    [Fact]
    public async Task Scanner_StopsReporting_OnceDisposed()
    {
        var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        var port = scanner.LocalPort;
        var count = 0;
        var first = new TaskCompletionSource();
        scanner.DeviceDiscovered += (_, _) =>
        {
            Interlocked.Increment(ref count);
            first.TrySetResult();
        };
        scanner.Start();
        var datagram = new DiscoveryAnnouncement(DeviceId, "PC-04", 17843, Thumbprint).Encode();
        SendRaw(port, datagram);
        await first.Task.WaitAsync(Patience);

        scanner.Dispose();
        scanner.Dispose();      // twice is fine
        var before = Volatile.Read(ref count);
        SendRaw(port, datagram);
        await Task.Delay(200);

        Assert.Equal(before, Volatile.Read(ref count));
        Assert.Throws<ObjectDisposedException>(() => scanner.Start());
    }

    /// <summary>
    /// "Found it, stop scanning" is the obvious thing for a handler to do. The receive loop is
    /// what is running the handler, so Dispose must not wait for the loop to finish.
    /// </summary>
    [Fact]
    public async Task DisposingTheScanner_FromItsOwnHandler_ReturnsPromptly()
    {
        var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        var elapsed = new TaskCompletionSource<TimeSpan>();
        scanner.DeviceDiscovered += (_, _) =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            scanner.Dispose();
            elapsed.TrySetResult(clock.Elapsed);
        };
        scanner.Start();

        SendRaw(scanner.LocalPort, new DiscoveryAnnouncement(DeviceId, "PC-04", 17843, Thumbprint).Encode());

        Assert.True(await elapsed.Task.WaitAsync(Patience) < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Scanner_ReportsTheEphemeralPortItBound()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);

        Assert.InRange(scanner.LocalPort, 1, 65535);
    }

    [Fact]
    public void Scanner_StartedTwice_Throws()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        scanner.Start();

        Assert.Throws<InvalidOperationException>(() => scanner.Start());
    }

    [Fact]
    public async Task Beacon_StopsSending_OnceDisposed()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var beacon = BeaconAt(port, intervalMs: 20);
        beacon.Start();
        await listener.ReceiveAsync().WaitAsync(Patience);   // it is sending

        beacon.Dispose();
        beacon.Dispose();       // twice is fine
        await Task.Delay(100);
        while (listener.Available > 0)
        {
            await listener.ReceiveAsync();      // drain what was already in flight
        }

        await Task.Delay(200);
        Assert.Equal(0, listener.Available);
    }

    [Theory]
    [InlineData("agent-1", 17843, Thumbprint)]
    [InlineData(DeviceId, 0, Thumbprint)]
    [InlineData(DeviceId, 65536, Thumbprint)]
    [InlineData(DeviceId, 17843, "xyz")]
    public void Beacon_RefusesToAnnounceSomethingNoScannerWouldAccept(string deviceId, int quicPort, string thumbprint)
    {
        Assert.Throws<ArgumentException>(
            () => new LanDiscoveryBeacon(deviceId, "PC-04", quicPort, thumbprint, 1234, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Beacon_RefusesADiscoveryPortOutOfRange(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LanDiscoveryBeacon(DeviceId, "PC-04", 17843, Thumbprint, port, 50));
    }

    [Fact]
    public void Beacon_RefusesAZeroInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LanDiscoveryBeacon(DeviceId, "PC-04", 17843, Thumbprint, 1234, 0));
    }

    [Fact]
    public void Beacon_StartedTwice_Throws()
    {
        using var scanner = new LanDiscoveryScanner(0, IPAddress.Loopback);
        using var beacon = BeaconTo(scanner);
        beacon.Start();

        Assert.Throws<InvalidOperationException>(() => beacon.Start());
    }
}
