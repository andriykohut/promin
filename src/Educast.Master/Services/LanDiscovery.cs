using System.Net;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;
using Educast.Protocol;
using Educast.Protocol.Discovery;

namespace Educast.Master.Services;

/// <summary>
/// <see cref="IDiscovery"/> over <see cref="LanDiscoveryScanner"/> on <see cref="EducastPorts.Discovery"/>.
/// </summary>
/// <remarks>
/// A scanner cannot be restarted once disposed, so each <see cref="Start"/> makes a new one and
/// <see cref="Stop"/> disposes it. Both are called on the UI thread — never from inside a
/// <c>DeviceDiscovered</c> handler, which runs on the scanner's own thread.
/// </remarks>
public sealed class LanDiscovery : IDiscovery
{
    private LanDiscoveryScanner? _scanner;

    public event Action<StudentPc>? Seen;

    public void Start()
    {
        if (_scanner is not null)
        {
            return;
        }

        var scanner = new LanDiscoveryScanner(EducastPorts.Discovery);
        scanner.DeviceDiscovered += OnDiscovered;
        _scanner = scanner;
        scanner.Start();
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

    private void OnDiscovered(DiscoveryAnnouncement announcement, IPAddress address) =>
        Seen?.Invoke(new StudentPc(
            announcement.DeviceId, announcement.DisplayName, address.ToString(), announcement.QuicPort));
}
