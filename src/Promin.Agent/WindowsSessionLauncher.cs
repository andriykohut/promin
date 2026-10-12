using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// Starts the agent in a signed-in Windows session from the service (which runs as SYSTEM): it duplicates the
/// service's own SYSTEM token, retargets it to that session, and creates the process on the session's interactive
/// desktop. So the agent runs as SYSTEM but on the user's screen — the identity the spec wants (it reads and writes
/// the data folder that install locks to SYSTEM and administrators), the way PsExec's "-s -i" runs a process.
/// Launching with the signed-in user's token instead (WTSQueryUserToken) would run the agent as that user, who is
/// locked out of the data folder. It needs a real multi-session OS, so it is proven by the hand-tests, not a unit test.
/// </summary>
internal sealed class WindowsSessionLauncher(string executablePath, Action<string> log) : ISessionLauncher
{
    public IAgentProcess LaunchInSession(uint sessionId)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_FOR_SOURCE, out var serviceToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the service's own token.");
        }

        var primaryToken = nint.Zero;
        var environment = nint.Zero;
        try
        {
            if (!DuplicateTokenEx(serviceToken, TOKEN_FOR_PRIMARY, nint.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the service's token.");
            }

            // Move the SYSTEM token into the interactive session so the agent appears on that session's desktop.
            var target = sessionId;
            if (!SetTokenInformation(primaryToken, TokenSessionId, ref target, sizeof(uint)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not set the token's session to {sessionId}.");
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                environment = nint.Zero; // the agent can still start, just without a loaded environment block
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

            CloseHandle(serviceToken);
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

        // The logon screen (nobody signed in) reports an empty user name; a signed-in session reports the user even
        // when it is locked, so the agent is still launched there and kept running behind the lock screen.
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
