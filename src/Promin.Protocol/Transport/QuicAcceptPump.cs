using System.Diagnostics;
using System.Net.Quic;

namespace Promin.Protocol.Transport;

/// <summary>
/// The loop that takes inbound connections off a QUIC listener, kept apart from the listener so
/// it can be driven by an accept source that fails on demand.
/// </summary>
/// <remarks>
/// .NET reports a failed inbound handshake by throwing out of <c>AcceptConnectionAsync</c>, so a
/// naive accept loop dies on the first stranger who dials the port.
///
/// <b>This loop does not try to tell a peer's failure from a local one by looking at the
/// exception.</b> An earlier version did, and was wrong in both directions against the real
/// runtime: a local credential that will not load surfaces as <c>AuthenticationException</c>,
/// the same type as a refused peer, while <c>QuicError.InternalError</c> — which looks local —
/// is something a remote peer can induce. There is no table of exception types that gets this
/// right, so there is no table. Instead:
///
/// <b>Nothing that comes out of accept stops the loop.</b> Every failure is reported to the
/// observer and the loop goes round again. The only way out is cancellation, which is how the
/// owning listener shuts it down. A remote peer therefore cannot end it, whatever it sends.
///
/// <b>A local credential fault is caught somewhere else, earlier, and loudly</b>: the listener
/// proves its own credential with a handshake against itself before <c>StartAsync</c> returns
/// (see <see cref="QuicAgentListener.StartAsync"/>), so that fault never reaches this loop as a
/// stream of anonymous "rejections".
///
/// <b>A refused dial is consumed at once.</b> Each failure is one real handshake that was
/// already waiting in the listener's queue; pausing between them only makes a legitimate master
/// wait behind strangers. There is no backoff.
///
/// <b>A failing accept still cannot spin a core.</b> The one case left to guard is an accept
/// source that fails instantly and for ever with no peer behind it. That is bounded by rate,
/// not by delay per failure: up to <see cref="MaxFailuresPerInterval"/> failures are consumed
/// in any <see cref="FailureInterval"/> with no pause at all, and only past that does the loop
/// wait out the remainder of the interval. Real refused handshakes cost the agent's own CPU a
/// full TLS exchange each and do not arrive at that rate; a spinning accept does.
///
/// The observer belongs to someone else (the pairing UI). If it throws, that is its bug; it is
/// not allowed to become the listener's.
/// </remarks>
internal sealed class QuicAcceptPump
{
    /// <summary>
    /// How many accept failures are consumed without any pause inside one
    /// <see cref="FailureInterval"/>. With the interval this caps a failing accept at 2,000
    /// iterations a second.
    /// </summary>
    internal const int MaxFailuresPerInterval = 200;

    /// <summary>The window the failure budget is counted over, and the longest single pause.</summary>
    internal static readonly TimeSpan FailureInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<CancellationToken, ValueTask<QuicConnection>> _accept;
    private readonly Func<QuicConnection, CancellationToken, ValueTask> _deliver;
    private readonly Action<Exception>? _acceptFailed;
    private int _consecutiveFailures;

    public QuicAcceptPump(
        Func<CancellationToken, ValueTask<QuicConnection>> accept,
        Func<QuicConnection, CancellationToken, ValueTask> deliver,
        Action<Exception>? acceptFailed)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(deliver);

        _accept = accept;
        _deliver = deliver;
        _acceptFailed = acceptFailed;
    }

    /// <summary>Accept failures since the last connection that was accepted.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled. That is the only way this returns, and it
    /// never throws: whatever accept or delivery throws is reported and the loop continues.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var intervalStart = Stopwatch.GetTimestamp();
        var failuresInInterval = 0;

        while (!ct.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await _accept(ct).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _consecutiveFailures);
                Report(failure);

                if (Stopwatch.GetElapsedTime(intervalStart) >= FailureInterval)
                {
                    intervalStart = Stopwatch.GetTimestamp();
                    failuresInInterval = 0;
                }

                if (++failuresInInterval >= MaxFailuresPerInterval)
                {
                    var remaining = FailureInterval - Stopwatch.GetElapsedTime(intervalStart);
                    if (remaining > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(remaining, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }

                    intervalStart = Stopwatch.GetTimestamp();
                    failuresInInterval = 0;
                }

                continue;
            }

            Volatile.Write(ref _consecutiveFailures, 0);

            try
            {
                await _deliver(connection, ct).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // Nobody took ownership of the connection, so it is closed here.
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                Report(failure);
            }
        }
    }

    private void Report(Exception failure)
    {
        if (_acceptFailed is null)
        {
            return;
        }

        try
        {
            _acceptFailed(failure);
        }
        catch (Exception)
        {
            // The observer threw. That is the observer's bug, and it must not stop this machine
            // accepting connections; there is nobody further up to tell from inside the loop.
        }
    }

    private static async ValueTask DisposeQuietlyAsync(QuicConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }
}
