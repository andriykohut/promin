namespace Promin.Agent;

/// <summary>
/// Keeps one agent running in whichever session is on the console. It launches the agent, and on each of the
/// agent exiting, the session ending or a user switching, or being told to stop, it does the one right thing:
/// restart with a growing delay, idle until the next sign-in, follow the switch, or end the agent. The agent
/// itself is unchanged and does not know a service started it. An agent that exits with
/// <see cref="ExitCodes.UpdateReady"/> has saved an accepted update: <paramref name="startUpdate"/> checks it and starts
/// its setup, which stops this service, so the agent is started again only if the setup has not done so within
/// <see cref="SetupGrace"/>.
/// </summary>
/// <param name="startUpdate">True when an accepted update's setup has started. Never throws; null starts none.</param>
internal sealed class AgentSupervisor(
    ISessionLauncher launcher, IConsoleSessions sessions, RestartBackoff backoff, TimeProvider time, Action<string> log,
    Func<bool>? startUpdate = null)
{
    /// <summary>How long a started setup has to stop the service before the agent is started again anyway.</summary>
    public static readonly TimeSpan SetupGrace = TimeSpan.FromMinutes(2);

    // The agent must stay up at least this long for the next exit to count as a fresh problem rather than a continuing one.
    private static readonly TimeSpan Stable = RestartBackoff.Longest;

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            // One cancellation scope per iteration, linked to stop; it cancels whichever wait loses below.
            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stop);

            // Arm the change-wait BEFORE reading the session, so a change that lands between the read and the wait
            // still completes this wait instead of being lost (which would strand the PC until the next change).
            var changed = sessions.WaitForChangeAsync(iteration.Token);
            var session = sessions.ActiveSession;

            if (session is null)
            {
                await Swallow(changed).ConfigureAwait(false); // idle until someone signs in (or stop)
                continue;
            }

            var exit = await RunOneAsync(session.Value, changed, iteration, stop).ConfigureAwait(false);
            if (exit is not { } code || stop.IsCancellationRequested)
            {
                continue;
            }

            if (code == ExitCodes.UpdateReady && startUpdate?.Invoke() == true)
            {
                log("The agent accepted an update; its setup is running.");
                await Swallow(Task.Delay(SetupGrace, time, stop)).ConfigureAwait(false);
            }
            else if (sessions.ActiveSession == session)
            {
                await Swallow(Task.Delay(backoff.NextDelay(), time, stop)).ConfigureAwait(false);
            }
        }
    }

    // An exit code → the agent is gone on its own (it exited or crashed; a launch that failed counts as Unexpected): the
    //         caller may back off and relaunch the same session. null → it was ended deliberately (stop, sign-out or a
    //         switch): the caller loops and re-evaluates the session with no backoff.
    private async Task<int?> RunOneAsync(uint session, Task changed, CancellationTokenSource iteration, CancellationToken stop)
    {
        IAgentProcess process;
        try
        {
            process = launcher.LaunchInSession(session);
        }
        catch (Exception e)
        {
            log($"The agent could not be started in session {session}: {e.Message}");
            await iteration.CancelAsync().ConfigureAwait(false);
            await Swallow(changed).ConfigureAwait(false); // the armed wait is ours to observe even when the launch failed
            return ExitCodes.Unexpected;
        }

        using (process)
        {
            var startedAt = time.GetTimestamp();
            var exited = process.WaitForExitAsync(iteration.Token);
            await Task.WhenAny(exited, changed).ConfigureAwait(false);
            await iteration.CancelAsync().ConfigureAwait(false);
            await Swallow(exited).ConfigureAwait(false);
            await Swallow(changed).ConfigureAwait(false);

            if (stop.IsCancellationRequested)
            {
                process.Kill();
                return null;
            }

            // Decide by whether the process actually exited, never by the session value: a flip away and back to the
            // same session (1->2->1) leaves ActiveSession unchanged yet the agent still running, and must not be read
            // as an exit. After the round is cancelled, a process that had exited stays completed; one that had not is
            // completed as cancelled, so IsCompletedSuccessfully tells the two apart.
            if (exited.IsCompletedSuccessfully)
            {
                if (time.GetElapsedTime(startedAt) >= Stable)
                {
                    backoff.Reset();
                }

                log($"The agent in session {session} exited with {process.ExitCode}.");
                return process.ExitCode;
            }

            // The change-wait won while the agent is still alive: end it, and let the caller re-read the session.
            process.Kill();
            return null;
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
