namespace Promin.Agent;

/// <summary>Stops and restarts the screen pictures, which share the one screen capture a process can hold with the control video.</summary>
internal interface IPreviewSwitch
{
    /// <summary>Returns once the pictures have stopped and their capture is released. Does nothing when they are stopped already.</summary>
    Task PauseAsync();

    /// <summary>Starts the pictures again. Does nothing when they are running.</summary>
    void Resume();
}

/// <summary>
/// Runs <paramref name="run"/> (the picture sender) from construction until <see cref="PauseAsync"/>, and again after
/// <see cref="Resume"/>. <paramref name="run"/> returns when its token is cancelled, or by itself, and throws nothing;
/// a run that ended by itself counts as stopped.
/// </summary>
internal sealed class PreviewSwitch : IPreviewSwitch, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _run;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _running;
    private Task _task = Task.CompletedTask;

    public PreviewSwitch(Func<CancellationToken, Task> run)
    {
        _run = run;
        Resume();
    }

    public async Task PauseAsync()
    {
        Task stopping;
        lock (_gate)
        {
            _running?.Cancel();
            _running = null;
            stopping = _task;
        }

        await stopping.ConfigureAwait(false);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_running is not null)
            {
                return;
            }

            var running = new CancellationTokenSource();
            _running = running;
            var previous = _task;
            _task = Task.Run(
                async () =>
                {
                    // The capture of the run being stopped is let go before the next run opens its own.
                    await previous.ConfigureAwait(false);
                    await _run(running.Token).ConfigureAwait(false);

                    // A run can end by itself (a send failed): letting go of it keeps Pause off the disposed source and
                    // lets Resume start another.
                    lock (_gate)
                    {
                        if (_running == running)
                        {
                            _running = null;
                        }
                    }

                    running.Dispose();
                },
                CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync() => new(PauseAsync());
}
