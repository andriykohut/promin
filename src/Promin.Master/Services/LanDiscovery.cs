using System.Net;
using System.Net.Sockets;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;
using Promin.Protocol;
using Promin.Protocol.Discovery;

namespace Promin.Master.Services;

/// <summary>
/// <see cref="IDiscovery"/> over <see cref="LanDiscoveryScanner"/> on <see cref="ProminPorts.Discovery"/>.
/// </summary>
/// <remarks>
/// A scanner cannot be restarted once disposed, so each <see cref="Start"/> makes a new one and
/// <see cref="Stop"/> disposes it. Both are called on the UI thread — never from inside a
/// <c>DeviceDiscovered</c> handler, which runs on the scanner's own thread. The scanner's
/// constructor throws <see cref="SocketException"/> when the port is already bound (a second
/// console on this PC); that is reported as <c>false</c>, not thrown into a navigation.
/// </remarks>
public sealed class LanDiscovery(Func<string, bool> isPinned) : IDiscovery
{
    private readonly AnnouncementFilter _filter = new(isPinned);
    private LanDiscoveryScanner? _scanner;

    public event Action<StudentPc>? Seen;

    public bool Start()
    {
        if (_scanner is not null)
        {
            return true;
        }

        LanDiscoveryScanner scanner;
        try
        {
            scanner = new LanDiscoveryScanner(ProminPorts.Discovery);
        }
        catch (SocketException)
        {
            return false;
        }

        scanner.DeviceDiscovered += OnDiscovered;
        _scanner = scanner;
        scanner.Start();
        return true;
    }

    public void Stop()
    {
        var scanner = _scanner;
        _scanner = null;
        if (scanner is null)
        {
            return;
        }

        scanner.DeviceDiscovered -= OnDiscovered;
        scanner.Dispose();
    }

    private void OnDiscovered(DiscoveryAnnouncement announcement, IPAddress address)
    {
        var pc = new StudentPc(announcement.DeviceId, announcement.DisplayName, address.ToString(), announcement.QuicPort);

        // Every announcement passed on is work for the UI thread; the filter decides which are worth it.
        bool post;
        lock (_filter)
        {
            post = _filter.ShouldPost(pc, Environment.TickCount64);
        }

        if (post)
        {
            Seen?.Invoke(pc);
        }
    }
}
