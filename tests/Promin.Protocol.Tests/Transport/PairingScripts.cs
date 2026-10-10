using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

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

    /// <summary>
    /// The master's half of the J-PAKE rounds, over a raw stream, in one go. Returns the session
    /// key this side ends up with, which is the agent's too if and only if the codes match. Use
    /// <see cref="StartMasterJPakeAsync"/> instead when the agent's end is not running yet: the
    /// first write is also what makes the stream visible to the agent.
    /// </summary>
    public static async Task<byte[]> MasterJPakeAsync(QuicStream stream, byte[] code, CancellationToken ct) =>
        await (await StartMasterJPakeAsync(stream, code, ct)).FinishAsync(ct);

    /// <summary>Sends round 1 and returns the rest of the exchange, to be finished once the agent runs.</summary>
    public static async Task<RawJPakeMaster> StartMasterJPakeAsync(QuicStream stream, byte[] code, CancellationToken ct)
    {
        var exchange = new JPakeExchange(JPakeRole.Master, code);
        await WriteAsync(stream, exchange.CreateRound1(), ct);
        return new RawJPakeMaster(stream, exchange);
    }

    /// <summary>The agent's half: take the master's round 1, send round 1, take round 2, send round 2.</summary>
    public static async Task<byte[]> AgentJPakeAsync(QuicStream stream, byte[] code, CancellationToken ct)
    {
        var exchange = new JPakeExchange(JPakeRole.Agent, code);

        // BouncyCastle wants an agent's own round created before the peer's is validated.
        var peerRound1 = Assert.IsType<PairJPakeRound1>(await ReadAsync(stream, ct));
        var round1 = exchange.CreateRound1();
        Assert.True(exchange.TryAcceptRound1(peerRound1));
        await WriteAsync(stream, round1, ct);

        var peerRound2 = Assert.IsType<PairJPakeRound2>(await ReadAsync(stream, ct));
        var round2 = exchange.CreateRound2();
        Assert.True(exchange.TryAcceptRound2(peerRound2));
        await WriteAsync(stream, round2, ct);

        return exchange.DeriveSessionKey();
    }

    /// <summary>
    /// What a stranger who has been admitted to pair does: J-PAKE with <paramref name="code"/>,
    /// then a challenge of its own, then reading the agent's answer. The agent's answer is the
    /// proof that it was admitted. Returns what the stranger holds afterwards.
    /// </summary>
    public static async Task<(byte[] SessionKey, byte[] MasterNonce, PairChallengeResponse Answer)> ChallengeAsync(
        QuicStream stream, byte[] code, CancellationToken ct)
    {
        var sessionKey = await MasterJPakeAsync(stream, code, ct);
        var masterNonce = PairingCrypto.CreateNonce();
        await WriteAsync(stream, new PairChallengeRequest(masterNonce), ct);
        var answer = Assert.IsType<PairChallengeResponse>(await ReadAsync(stream, ct));
        return (sessionKey, masterNonce, answer);
    }
}

/// <summary>A master's J-PAKE exchange that has sent round 1 and is waiting for the agent to start.</summary>
internal sealed class RawJPakeMaster(QuicStream stream, JPakeExchange exchange)
{
    /// <summary>Takes the agent's round 1, sends round 2, takes the agent's round 2; returns the session key.</summary>
    public async Task<byte[]> FinishAsync(CancellationToken ct)
    {
        Assert.True(exchange.TryAcceptRound1(Assert.IsType<PairJPakeRound1>(await RawPairing.ReadAsync(stream, ct))));
        await RawPairing.WriteAsync(stream, exchange.CreateRound2(), ct);
        Assert.True(exchange.TryAcceptRound2(Assert.IsType<PairJPakeRound2>(await RawPairing.ReadAsync(stream, ct))));
        return exchange.DeriveSessionKey();
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
