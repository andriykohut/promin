using System.Net;
using System.Net.Sockets;

namespace Promin.Protocol.Discovery;

/// <summary>
/// The console's side of discovery: listens for the announcements agent PCs broadcast and
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
/// handler that throws is ignored: one faulty subscriber must not stop discovery. A handler may
/// dispose the scanner ("found it, stop looking"); that returns at once and does not wait for
/// the receive loop, which is the thing running the handler.
///
/// Datagrams are received into one fixed buffer of <see cref="DiscoveryAnnouncement.MaxDatagramBytes"/>
/// plus one byte, so a flood of large datagrams costs no allocation; a datagram that does not
/// fit is refused.
/// </remarks>
public sealed class LanDiscoveryScanner : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>True on the thread that is running a subscriber, which is the receive loop's own thread.</summary>
    [ThreadStatic]
    private static bool t_inHandler;

    private readonly Socket _socket;
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

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, discoveryPort));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>Raised for each well-formed announcement, with the address it was sent from.</summary>
    public event Action<DiscoveryAnnouncement, IPAddress>? DeviceDiscovered;

    /// <summary>The port actually bound, which resolves port 0 to the one the OS chose.</summary>
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

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
        // One byte more than the largest datagram worth reading: a datagram that fills it is too big.
        var buffer = new byte[DiscoveryAnnouncement.MaxDatagramBytes + 1];
        EndPoint anySender = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anySender, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ConnectionReset: Windows reports an ICMP "port unreachable" from an earlier send
                // as a failed receive on a UDP socket; it says nothing about incoming datagrams.
                // MessageSize: a datagram too big for the buffer, which is refused and gone.
                continue;
            }
            catch (SocketException)
            {
                // Something else is wrong with the socket. Wait a moment and try again rather than
                // going deaf for good, and rather than spinning.
                try
                {
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (received.RemoteEndPoint is IPEndPoint sender
                && DiscoveryAnnouncement.TryDecode(buffer.AsSpan(0, received.ReceivedBytes), out var announcement))
            {
                Raise(announcement, sender.Address);
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
            t_inHandler = true;
            try
            {
                handler(announcement, from);
            }
            catch (Exception)
            {
                // A faulty subscriber must not stop discovery, nor starve the other subscribers.
            }
            finally
            {
                t_inHandler = false;
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
            // Not from inside a subscriber: that is the loop itself, and it cannot finish while the
            // subscriber is still waiting for Dispose to return.
            if (loop is not null && !t_inHandler)
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
