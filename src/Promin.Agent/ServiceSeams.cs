namespace Promin.Agent;

/// <summary>The agent process the supervisor is watching.</summary>
internal interface IAgentProcess : IDisposable
{
    /// <summary>Completes when the process exits, or throws <see cref="OperationCanceledException"/> if the wait is cancelled first (the process is not ended by cancelling the wait).</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Ends the process now if it is still running. Never throws.</summary>
    void Kill();
}

/// <summary>Starts the agent in a Windows session.</summary>
internal interface ISessionLauncher
{
    /// <summary>Starts <c>Promin.Agent.exe</c> (no arguments: a full-screen agent) in session <paramref name="sessionId"/>. Throws if it cannot be started.</summary>
    IAgentProcess LaunchInSession(uint sessionId);
}

/// <summary>Which session is on the console, and a way to await the next change.</summary>
internal interface IConsoleSessions
{
    /// <summary>The session on the console now, or null when nobody is signed in.</summary>
    uint? ActiveSession { get; }

    /// <summary>Completes the next time <see cref="ActiveSession"/> changes (sign-in, sign-out, or a user switch).</summary>
    Task WaitForChangeAsync(CancellationToken ct);
}
