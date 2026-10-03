using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Messages;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// Speaking the control protocol by hand over a raw QUIC stream, for tests that have to play
/// one end of a pairing themselves — a rogue, a peer that stops half-way, a peer that answers
/// correctly and then misbehaves.
/// </summary>
internal static class RawPairing
{
    public static ValueTask WriteAsync(QuicStream stream, ControlMessage message, CancellationToken ct) =>
        stream.WriteAsync(ControlMessageCodec.Encode(message), ct);

    /// <summary>Reads exactly one length-prefixed control message, and not a byte more.</summary>
    public static async Task<ControlMessage> ReadAsync(QuicStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);

        var frame = new byte[4 + BinaryPrimitives.ReadInt32BigEndian(header)];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), ct);

        Assert.True(ControlMessageCodec.TryDecode(new ReadOnlySequence<byte>(frame), out var message, out _));
        return message!;
    }
}

/// <summary>
/// A stand-in for the agent that a real master can be pointed at: a QUIC listener presenting a
/// given certificate, which hands the master's control stream to a script. It lets a test make
/// the agent's end do exactly one particular thing at exactly one particular point — something
/// the real agent cannot be made to do on demand.
/// </summary>
internal sealed class ScriptedAgent : IAsyncDisposable
{
    private readonly QuicListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private ScriptedAgent(QuicListener listener, Func<QuicConnection, QuicStream, CancellationToken, Task> script)
    {
        _listener = listener;
        Finished = RunAsync(script, _stop.Token);
    }

    public IPEndPoint Endpoint => new(IPAddress.Loopback, _listener.LocalEndPoint.Port);

    /// <summary>Completes when the script has run; faults if the script threw.</summary>
    public Task Finished { get; }

    public static async Task<ScriptedAgent> StartAsync(
        X509Certificate2 certificate,
        Func<QuicConnection, QuicStream, CancellationToken, Task> script,
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
                        ServerCertificate = certificate,
                        ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    },
                }),
            },
            ct);

        return new ScriptedAgent(listener, script);
    }

    private async Task RunAsync(Func<QuicConnection, QuicStream, CancellationToken, Task> script, CancellationToken ct)
    {
        await using var connection = await _listener.AcceptConnectionAsync(ct);
        await using var stream = await connection.AcceptInboundStreamAsync(ct);
        await script(connection, stream, ct);
    }

    /// <summary>Waits, without reading anything useful, until the peer has finished with the stream.</summary>
    public static async Task WaitForPeerToLeaveAsync(QuicStream stream, CancellationToken ct)
    {
        var sink = new byte[256];
        try
        {
            while (await stream.ReadAsync(sink, ct) > 0)
            {
            }
        }
        catch (QuicException)
        {
            // Aborted or disconnected: also "finished".
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Finished;
        }
        catch (Exception)
        {
            // A test that cares about the script's outcome awaits Finished itself.
        }

        await _listener.DisposeAsync();
        _stop.Dispose();
    }
}
