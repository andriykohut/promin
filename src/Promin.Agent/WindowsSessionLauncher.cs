using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// Starts the agent in a signed-in Windows session from the service (which runs as SYSTEM): it takes that session's
/// own user token and creates the process with it, on the session's interactive desktop, so the agent runs exactly
/// as a hand-started agent would. It needs a real multi-session OS, so it is proven by the hand-tests, not a unit test.
/// </summary>
internal sealed class WindowsSessionLauncher(string executablePath, Action<string> log) : ISessionLauncher
{
    public IAgentProcess LaunchInSession(uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"No signed-in user token for session {sessionId}.");
        }

        var primaryToken = nint.Zero;
        var environment = nint.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, TOKEN_FOR_PRIMARY, nint.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the session's user token.");
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                environment = nint.Zero; // the agent can still start, just without the user's environment
            }

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default", // the session's interactive desktop
            };

            var created = CreateProcessAsUserW(
                primaryToken,
                executablePath,
                null, // no arguments: a full-screen agent
                nint.Zero,
                nint.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT,
                environment,
                Path.GetDirectoryName(executablePath),
                ref startupInfo,
                out var processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start the agent in session {sessionId}.");
            }

            CloseHandle(processInfo.Thread);
            log($"Started the agent in session {sessionId} (process {processInfo.ProcessId}).");
            return new LaunchedProcess(processInfo.Process);
        }
        finally
        {
            if (environment != nint.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != nint.Zero)
            {
                CloseHandle(primaryToken);
            }

            CloseHandle(userToken);
        }
    }

    /// <summary>The console session when a user is signed in there, else null (nobody signed in, or the logon screen).</summary>
    public static uint? ActiveConsoleSessionOrNull()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveConsoleSession)
        {
            return null;
        }

        // A session with nobody signed in (the logon or lock screen) reports an empty user name.
        if (!WTSQuerySessionInformationW(nint.Zero, sessionId, WTSUserName, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer)) ? null : sessionId;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private sealed class LaunchedProcess(nint process) : IAgentProcess
    {
        private int _closed;

        public Task WaitForExitAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled(ct);
            }

            // The process handle is signalled when the process exits. A cancel while it is still running completes the
            // returned task as cancelled (it throws OperationCanceledException) without ending the process — the
            // contract AgentSupervisor relies on to tell "exited on its own" from "we cancelled to kill it".
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processSignal = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
            var registration = ThreadPool.RegisterWaitForSingleObject(
                processSignal, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            var cancel = ct.Register(() => completion.TrySetCanceled(ct));

            return completion.Task.ContinueWith(
                finished =>
                {
                    registration.Unregister(null);
                    cancel.Dispose();
                    processSignal.Dispose();
                    return finished;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }

        public void Kill()
        {
            try
            {
                TerminateProcess(process, 1); // harmless if it has already exited
            }
            catch
            {
                // Contract: Kill never throws.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                CloseHandle(process);
            }
        }
    }
}
