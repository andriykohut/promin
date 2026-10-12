namespace Promin.Agent;

/// <summary>
/// Keeps one agent running in whichever session is on the console. It launches the agent, and on each of the
/// agent exiting, the session ending or a user switching, or being told to stop, it does the one right thing:
/// restart with a growing delay, idle until the next sign-in, follow the switch, or end the agent. The agent
/// itself is unchanged and does not know a service started it.
/// </summary>
internal sealed class AgentSupervisor(
    ISessionLauncher launcher, IConsoleSessions sessions, RestartBackoff backoff, TimeProvider time, Action<string> log)
{
    // The agent must stay up at least this long for the next exit to count as a fresh problem rather than a continuing one.
    private static readonly TimeSpan Stable = RestartBackoff.Longest;

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var session = sessions.ActiveSession;
            if (session is null)
            {
                await Swallow(sessions.WaitForChangeAsync(stop)).ConfigureAwait(false);
                continue;
            }

            if (!await RunOneAsync(session.Value, stop).ConfigureAwait(false))
            {
                // The launch or the round failed; wait out the backoff before trying again (unless stopping or the session changed).
                if (!stop.IsCancellationRequested && sessions.ActiveSession == session)
                {
                    await Swallow(Task.Delay(backoff.NextDelay(), time, stop)).ConfigureAwait(false);
                }
            }
        }
    }

    // Returns true if the agent exited on its own after running (caller decides on backoff); false if it could not be launched.
    private async Task<bool> RunOneAsync(uint session, CancellationToken stop)
    {
        IAgentProcess process;
        try
        {
            process = launcher.LaunchInSession(session);
        }
        catch (Exception e)
        {
            log($"The agent could not be started in session {session}: {e.Message}");
            return false;
        }

        using (process)
        {
            var startedAt = time.GetTimestamp();
            using var round = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var exited = process.WaitForExitAsync(round.Token);
            var changed = sessions.WaitForChangeAsync(round.Token);
            await Task.WhenAny(exited, changed).ConfigureAwait(false);
            await round.CancelAsync().ConfigureAwait(false);
            await Swallow(exited).ConfigureAwait(false);
            await Swallow(changed).ConfigureAwait(false);

            if (stop.IsCancellationRequested || sessions.ActiveSession != session)
            {
                process.Kill(); // stopping, signed out, or switched: the agent goes with the session
                return true;
            }

            // The agent exited on its own with the same session still on the console.
            if (time.GetElapsedTime(startedAt) >= Stable)
            {
                backoff.Reset();
            }

            log($"The agent in session {session} exited; it will be started again.");
            return false;
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
