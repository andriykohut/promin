using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// A machine in the middle. It terminates the master's QUIC connection with a certificate of its
/// own, opens a second connection to the real agent with a certificate of its own, and copies
/// the control stream between the two byte for byte, changing nothing. It does not know the
/// pre-shared key and does not need to: if the handshake proofs say nothing about which
/// certificates are at the two ends, both ends accept, and each pins <i>the relay's</i>
/// certificate.
/// </summary>
internal sealed class PairingRelay : IAsyncDisposable
{
    private readonly QuicListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _running;
    private long _bytesToAgent;
    private long _bytesToMaster;

    private PairingRelay(
        QuicListener listener,
        IPEndPoint agentEndpoint,
        X509Certificate2 towardAgent,
        string? agentServerName)
    {
        _listener = listener;
        _running = RunAsync(agentEndpoint, towardAgent, agentServerName, _stop.Token);
    }

    /// <summary>Where the master is pointed instead of at the agent.</summary>
    public IPEndPoint Endpoint => new(IPAddress.Loopback, _listener.LocalEndPoint.Port);

    public long BytesForwardedToAgent => Interlocked.Read(ref _bytesToAgent);

    public long BytesForwardedToMaster => Interlocked.Read(ref _bytesToMaster);

    /// <param name="towardMaster">The certificate the relay shows the master, in the agent's place.</param>
    /// <param name="towardAgent">The certificate the relay shows the agent, in the master's place.</param>
    /// <param name="agentServerName">The TLS server name the relay uses toward the agent, as a master would.</param>
    public static async Task<PairingRelay> StartAsync(
        IPEndPoint agentEndpoint,
        X509Certificate2 towardMaster,
        X509Certificate2 towardAgent,
        string? agentServerName,
        CancellationToken ct)
    {
        var listener = await QuicListener.ListenAsync(
            new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    IdleTimeout = QuicTransportSettings.IdleTimeout,
                    MaxInboundBidirectionalStreams = 4,
                    MaxInboundUnidirectionalStreams = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = towardMaster,
                        ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    },
                }),
            },
            ct);

        return new PairingRelay(listener, agentEndpoint, towardAgent, agentServerName);
    }

    private async Task RunAsync(
        IPEndPoint agentEndpoint, X509Certificate2 towardAgent, string? agentServerName, CancellationToken ct)
    {
        try
        {
            await using var fromMaster = await _listener.AcceptConnectionAsync(ct);
            await using var toAgent = await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = agentEndpoint,
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    IdleTimeout = QuicTransportSettings.IdleTimeout,
                    MaxInboundBidirectionalStreams = 4,
                    MaxInboundUnidirectionalStreams = 0,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = agentServerName ?? agentEndpoint.Address.ToString(),
                        ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                        ClientCertificates = [towardAgent],
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    },
                },
                ct);

            // The master opens the control stream and speaks first; mirror that toward the agent.
            await using var masterStream = await fromMaster.AcceptInboundStreamAsync(ct);
            await using var agentStream = await toAgent.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);

            await Task.WhenAll(
                CopyAsync(masterStream, agentStream, toAgent: true, ct),
                CopyAsync(agentStream, masterStream, toAgent: false, ct));
        }
        catch (Exception)
        {
            // Either end hung up, which is how every run of this ends. The test asserts on what
            // the two real machines concluded, not on how the relay's connections died.
        }
    }

    private async Task CopyAsync(QuicStream from, QuicStream to, bool toAgent, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    to.CompleteWrites();
                    return;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                if (toAgent)
                {
                    Interlocked.Add(ref _bytesToAgent, read);
                }
                else
                {
                    Interlocked.Add(ref _bytesToMaster, read);
                }
            }
        }
        catch (QuicException aborted) when (aborted.ApplicationErrorCode is { } code)
        {
            // Pass an abort through as an abort, as faithfully as everything else.
            to.Abort(QuicAbortDirection.Write, code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _running;
        }
        catch (Exception)
        {
        }

        await _listener.DisposeAsync();
        _stop.Dispose();
    }
}
