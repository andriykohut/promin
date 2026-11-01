using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;

namespace Promin.Agent.Tests;

/// <summary>The agent's end of a console PC's connection, over real QUIC on loopback.</summary>
public class QuicAgentConnectionTests
{
    private const string ConsoleId = "console-1";
    private const string AgentId = "agent-1";

    /// <summary>
    /// The network between the two ends, one UDP hop on loopback. Pulled, nothing crosses it any more, which from the
    /// agent's side is what a killed console PC looks like: nobody acknowledges anything.
    /// </summary>
    private sealed class Cable : IDisposable
    {
        private readonly UdpClient _towardConsole = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _towardAgent = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private volatile IPEndPoint? _console;
        private volatile bool _pulled;

        public Cable(IPEndPoint agent)
        {
            _ = CarryToAgentAsync(agent);
            _ = CarryToConsoleAsync();
        }

        public IPEndPoint Endpoint => (IPEndPoint)_towardConsole.Client.LocalEndPoint!;

        public void Pull() => _pulled = true;

        public void Dispose()
        {
            _stop.Cancel();
            _towardConsole.Dispose();
            _towardAgent.Dispose();
            _stop.Dispose();
        }

        private async Task CarryToAgentAsync(IPEndPoint agent)
        {
            try
            {
                while (true)
                {
                    var datagram = await _towardConsole.ReceiveAsync(_stop.Token);
                    _console = datagram.RemoteEndPoint;
                    if (!_pulled)
                    {
                        await _towardAgent.SendAsync(datagram.Buffer, agent, _stop.Token);
                    }
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        private async Task CarryToConsoleAsync()
        {
            try
            {
                while (true)
                {
                    var datagram = await _towardAgent.ReceiveAsync(_stop.Token);
                    if (!_pulled && _console is { } console)
                    {
                        await _towardConsole.SendAsync(datagram.Buffer, console, _stop.Token);
                    }
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>A console PC connected to an agent through a <see cref="Cable"/>, its streams open and sorted by the agent.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly X509Certificate2 _consoleCertificate = SelfSignedCertificateFactory.Create(ConsoleId);
        private readonly X509Certificate2 _agentCertificate = SelfSignedCertificateFactory.Create(AgentId);
        private readonly List<QuicControlChannel> _consoleStreams = [];
        private QuicAgentListener? _listener;
        private Cable? _cable;
        private QuicChannel? _consoleChannel;

        public QuicAgentConnection Connection { get; private set; } = null!;

        public void PullTheCable() => _cable!.Pull();

        public async Task ConnectAsync(CancellationToken ct)
        {
            _listener = new QuicAgentListener(_agentCertificate, Pins(ConsoleId, _consoleCertificate), port: 0, IPAddress.Loopback);
            await _listener.StartAsync(ct);
            _cable = new Cable(new IPEndPoint(IPAddress.Loopback, _listener.LocalEndPoint.Port));

            var accepted = _listener.AcceptAsync(ct);
            _consoleChannel = await new QuicMasterConnector(_consoleCertificate, Pins(AgentId, _agentCertificate))
                .ConnectAsync(_cable.Endpoint, AgentId, ct);
            Connection = new QuicAgentConnection(await accepted);

            await OpenStreamAsync(new SendFiles(1, Open: false), ct); // left queued: nothing takes it
            await OpenStreamAsync(new WatchLock(), ct);
            await OpenStreamAsync(new StopBroadcast(), ct);
            await Connection.AcceptLockStreamAsync(ct);
            await Connection.ReceiveControlAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_consoleChannel is not null)
            {
                await _consoleChannel.DisposeAsync();
            }

            foreach (var stream in _consoleStreams)
            {
                await stream.DisposeAsync();
            }

            _cable?.Dispose();
            if (_listener is not null)
            {
                await _listener.DisposeAsync();
            }

            _consoleCertificate.Dispose();
            _agentCertificate.Dispose();
            _temp.Dispose();
        }

        private async Task OpenStreamAsync(ControlMessage first, CancellationToken ct)
        {
            var stream = await _consoleChannel!.OpenControlChannelAsync(ct);
            _consoleStreams.Add(stream);
            await stream.SendAsync(first, ct);
        }

        private PeerCertificateStore Pins(string deviceId, X509Certificate2 certificate)
        {
            var file = _temp.File($"pins-{deviceId}.json");
            File.WriteAllText(file, $"{{\"{deviceId}\":\"{PeerCertificateStore.ComputeThumbprint(certificate)}\"}}");
            var pins = new PeerCertificateStore();
            pins.Load(file);
            return pins;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_ThrowsNothing_AndDoesNotWaitForAConsolePcThatHasGone(bool gone)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        await rig.ConnectAsync(ct);
        if (gone)
        {
            rig.PullTheCable();
        }

        await rig.Connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
