using System.Net;
using System.Net.Sockets;

namespace Educast.Protocol.Discovery;

/// <summary>
/// The console's side of discovery: listens for the announcements student PCs broadcast and
/// raises <see cref="DeviceDiscovered"/> for each well-formed one.
/// </summary>
/// <remarks>
/// <b>Every datagram is hostile input.</b> Anything on the LAN can send to this port, so a
/// datagram that does not decode (see <see cref="DiscoveryAnnouncement.TryDecode"/>) is dropped
/// without a trace, and a well-formed one is still only a claim about where to dial: the event
/// says nothing about whether the device is who it says it is. An announcement is reported once
/// per datagram, so the same device is reported repeatedly (the beacon repeats itself); the
/// consumer decides how to collapse that, and how to treat a sender that floods.
///
/// The socket binds <see cref="IPAddress.Any"/> unless a bind address is passed in, because a
/// broadcast is only delivered to a socket bound to the wildcard. Tests bind loopback.
///
/// <see cref="DeviceDiscovered"/> is raised on a thread-pool thread, one datagram at a time. A
/// handler that throws is ignored: one faulty subscriber must not stop discovery.
/// </remarks>
public sealed class LanDiscoveryScanner : IDisposable
{
    private readonly UdpClient _socket;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    /// <param name="discoveryPort">The UDP port to listen on; 0 lets the OS choose (see <see cref="LocalPort"/>).</param>
    /// <param name="bindAddress">
    /// The local address to listen on. <see langword="null"/> (what production uses) is every
    /// interface, which is what receiving a broadcast needs.
    /// </param>
    /// <exception cref="SocketException">The port is already in use.</exception>
    public LanDiscoveryScanner(int discoveryPort, IPAddress? bindAddress = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(discoveryPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(discoveryPort, 65535);

        _socket = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, discoveryPort));
    }

    /// <summary>Raised for each well-formed announcement, with the address it was sent from.</summary>
    public event Action<DiscoveryAnnouncement, IPAddress>? DeviceDiscovered;

    /// <summary>The port actually bound, which resolves port 0 to the one the OS chose.</summary>
    public int LocalPort => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The scanner has already been started.");
            }

            _started = true;
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an ICMP "port unreachable" from an earlier send as a failed
                // receive on a UDP socket. It says nothing about incoming datagrams.
                continue;
            }
            catch (SocketException)
            {
                return;
            }

            if (DiscoveryAnnouncement.TryDecode(received.Buffer, out var announcement))
            {
                Raise(announcement, received.RemoteEndPoint.Address);
            }
        }
    }

    private void Raise(DiscoveryAnnouncement announcement, IPAddress from)
    {
        var handlers = DeviceDiscovered;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<DiscoveryAnnouncement, IPAddress>>())
        {
            try
            {
                handler(announcement, from);
            }
            catch (Exception)
            {
                // A faulty subscriber must not stop discovery, nor starve the other subscribers.
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
            _socket.Dispose();
            loop = _loop;
        }

        try
        {
            // (Not from inside the loop itself, which a subscriber disposing the object would be.)
            if (loop is not null && Task.CurrentId != loop.Id)
            {
                loop.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (AggregateException)
        {
            // The loop only ever ends by cancellation or disposal.
        }

        _cts.Dispose();
    }
}
