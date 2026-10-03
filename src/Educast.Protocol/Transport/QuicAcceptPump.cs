using System.Net.Quic;
using System.Security.Authentication;

namespace Educast.Protocol.Transport;

/// <summary>
/// The loop that takes inbound connections off a QUIC listener, kept apart from the listener so
/// it can be driven by an accept source that fails on demand.
/// </summary>
/// <remarks>
/// .NET reports a failed inbound handshake by throwing out of <c>AcceptConnectionAsync</c>, so a
/// naive accept loop dies on the first stranger who dials the port. This one separates three
/// things that the earlier loop treated as one:
///
/// <b>A peer that was refused</b> (<see cref="IsPeerHandshakeFailure"/>) is routine. It is
/// reported, counted, and the loop carries on. It is never fatal, however many times in a row it
/// happens — otherwise anyone on the LAN could take an agent off the air by dialling it a few
/// times with the wrong certificate.
///
/// <b>A run of failures is slowed down</b> (<see cref="BackoffAfter"/>). An accept source that
/// fails instantly and persistently would otherwise spin a core. The first two consecutive
/// failures cost nothing, so an isolated stranger never delays the legitimate master behind it;
/// after that the delay doubles from 25 ms to a 500 ms cap, and resets on the next success.
///
/// <b>A fault of this machine's own is not a rejected peer</b> and is not absorbed. A credential
/// that will not load, a connection-options callback that throws, a listener disposed underneath
/// the loop: these end <see cref="RunAsync"/> with the exception, so the owner sees a broken
/// listener instead of one that silently "rejects" everyone forever.
///
/// The rejection callback belongs to someone else (the pairing UI). If it throws, that is its
/// bug; it is not allowed to become the listener's.
/// </remarks>
internal sealed class QuicAcceptPump
{
    private const int FailuresBeforeBackoff = 2;
    private const int FirstBackoffMilliseconds = 25;

    private readonly Func<CancellationToken, ValueTask<QuicConnection>> _accept;
    private readonly Func<QuicConnection, CancellationToken, ValueTask> _deliver;
    private readonly Action<Exception>? _connectionRejected;
    private int _consecutiveFailures;

    public QuicAcceptPump(
        Func<CancellationToken, ValueTask<QuicConnection>> accept,
        Func<QuicConnection, CancellationToken, ValueTask> deliver,
        Action<Exception>? connectionRejected)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(deliver);

        _accept = accept;
        _deliver = deliver;
        _connectionRejected = connectionRejected;
    }

    /// <summary>The longest the pump ever waits between two accept attempts.</summary>
    public static TimeSpan MaxBackoff { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Refused handshakes since the last connection that was accepted.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled, which is a normal return. Any other way
    /// out is a local fault, thrown to the caller.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            QuicConnection connection;
            try
            {
                ct.ThrowIfCancellationRequested();
                connection = await _accept(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure) when (IsPeerHandshakeFailure(failure))
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                Report(failure);

                var delay = BackoffAfter(failures);
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                continue;
            }

            Volatile.Write(ref _consecutiveFailures, 0);

            try
            {
                await _deliver(connection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// True for the failures a remote peer can cause while a connection is being accepted: a
    /// certificate this side refused or a TLS alert the peer sent
    /// (<see cref="AuthenticationException"/>), and a connection the peer aborted, abandoned or
    /// broke at the transport level. Everything else — notably
    /// <see cref="QuicError.InternalError"/> and <see cref="QuicError.CallbackError"/>, which
    /// is how a local credential or options failure appears — is not a rejected peer.
    /// </summary>
    internal static bool IsPeerHandshakeFailure(Exception failure) => failure switch
    {
        AuthenticationException => true,
        QuicException quic => quic.QuicError
            is QuicError.ConnectionAborted
            or QuicError.ConnectionTimeout
            or QuicError.ConnectionRefused
            or QuicError.ConnectionIdle
            or QuicError.VersionNegotiationError
            or QuicError.TransportError,
        _ => false,
    };

    /// <summary>
    /// How long to wait before the next accept attempt, given how many have failed in a row.
    /// </summary>
    internal static TimeSpan BackoffAfter(int consecutiveFailures)
    {
        var doublings = consecutiveFailures - FailuresBeforeBackoff - 1;
        if (doublings < 0)
        {
            return TimeSpan.Zero;
        }

        // 25 ms << 5 is already past the cap; the guard also keeps the shift from overflowing.
        if (doublings >= 5)
        {
            return MaxBackoff;
        }

        var delay = TimeSpan.FromMilliseconds(FirstBackoffMilliseconds << doublings);
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    private void Report(Exception failure)
    {
        if (_connectionRejected is null)
        {
            return;
        }

        try
        {
            _connectionRejected(failure);
        }
        catch (Exception)
        {
            // The observer threw. That is the observer's bug, and it must not stop this machine
            // accepting connections; there is nobody further up to tell from inside the loop.
        }
    }
}
