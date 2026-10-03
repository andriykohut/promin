using System.Net;
using System.Net.Sockets;

namespace Educast.Protocol.Discovery;

/// <summary>
/// The student PC's side of discovery: announces this device on the LAN, over and over, so a
/// teacher's console that starts listening at any time finds it within one interval.
/// </summary>
/// <remarks>
/// The announcement is sent to <see cref="IPAddress.Broadcast"/> (255.255.255.255) on
/// the discovery port unless a destination is passed in; tests pass a loopback
/// address so that nothing ever goes onto a real network. When the destination is a loopback
/// address the sending socket is bound to loopback as well, rather than to every interface:
/// there is nothing a wildcard-bound socket could reach that a loopback destination can use,
/// and binding one is what makes Windows Firewall ask the user a question.
///
/// A send that fails (the network cable is out, the adapter is resetting) is skipped and tried
/// again at the next interval; a laptop that loses its network for a minute resumes announcing
/// by itself. The announcement says only where to dial: see <see cref="DiscoveryAnnouncement"/>.
/// </remarks>
public sealed class LanDiscoveryBeacon : IDisposable
{
    private readonly byte[] _datagram;
    private readonly int _discoveryPort;
    private readonly int _intervalMs;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _socket;
    private Task? _loop;
    private bool _disposed;

    /// <param name="deviceId">The install's device id (32 lowercase hex characters).</param>
    /// <param name="displayName">What a person reads; the machine name on a real agent.</param>
    /// <param name="quicPort">Where this device accepts QUIC connections.</param>
    /// <param name="certificateThumbprint">The device certificate's thumbprint, 40 or 64 hex characters.</param>
    /// <param name="discoveryPort">The UDP port the console listens on.</param>
    /// <param name="intervalMs">How often to announce.</param>
    /// <param name="destination">
    /// Where to send. <see langword="null"/> (what production uses) is the LAN broadcast address.
    /// </param>
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
        int intervalMs = 2000,
        IPAddress? destination = null)
    {
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
        Destination = destination ?? IPAddress.Broadcast;
    }

    /// <summary>The address announcements are sent to.</summary>
    internal IPAddress Destination { get; }

    /// <summary>Begins announcing: once straight away, then every interval, until disposed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null)
            {
                throw new InvalidOperationException("The beacon has already been started.");
            }

            var local = IPAddress.IsLoopback(Destination) ? IPAddress.Loopback : IPAddress.Any;
            _socket = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };

            var socket = _socket;
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(socket, token));
        }
    }

    private async Task RunAsync(UdpClient socket, CancellationToken ct)
    {
        var destination = new IPEndPoint(Destination, _discoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await socket.SendAsync(_datagram, destination, ct).ConfigureAwait(false);
                await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // No network right now. Try again at the next interval rather than going quiet.
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
            _socket?.Dispose();
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
