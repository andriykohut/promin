namespace Promin.Agent;

/// <summary>
/// One connection at a time. When the console PC connects again (it restarted, or its network came back) its
/// old connection can still look alive for the 25 seconds of QUIC's idle timeout; the newer one replaces it at
/// once, so the agent PC's screen does not wait and two windows are never made.
/// </summary>
internal sealed class NewestConnectionWins
{
    private CancellationTokenSource? _currentCts;
    private Task? _current;

    /// <summary>
    /// Cancels the connection being handled, waits for it to finish (its display is removed by then), and starts
    /// <paramref name="run"/> as the current one. Not awaited for its whole life: it returns once <paramref name="run"/> has started.
    /// </summary>
    public async Task StartAsync(Func<CancellationToken, Task> run, CancellationToken shutdown)
    {
        await StopAsync().ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _currentCts = cts;
        _current = Task.Run(() => run(cts.Token), CancellationToken.None);
    }

    /// <summary>Cancels the current connection, if any, and waits for it to finish. Whatever it threw is not rethrown.</summary>
    public async Task StopAsync()
    {
        var (cts, current) = (_currentCts, _current);
        (_currentCts, _current) = (null, null);
        if (cts is null || current is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await current.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Its own handler logs what went wrong with it.
        }
        finally
        {
            cts.Dispose();
        }
    }
}
