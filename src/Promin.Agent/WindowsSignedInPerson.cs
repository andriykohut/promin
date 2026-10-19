using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// The person signed in to the agent's own session, reached through their token: the agent runs as SYSTEM, so it
/// asks Windows for the token of whoever is signed in there (WTSQueryUserToken) on every call and closes it after.
/// That token is the person's limited one even when they are an administrator, so nothing started with it is
/// elevated. When the agent is not SYSTEM (a --windowed run from a terminal), Windows refuses the token and this
/// acts as the agent's own user instead, unless that user is an elevated administrator: then every member throws
/// <see cref="Win32Exception"/> (1314), so nothing is written or started with administrator rights. It needs a real
/// signed-in session, so it is proven by the hand-tests, not a unit test.
/// </summary>
internal sealed class WindowsSignedInPerson(string executablePath) : ISignedInPerson
{
    private readonly uint _sessionId = OwnSessionId();

    public string? Name
    {
        get
        {
            if (!TryTakeToken(out var token))
            {
                return null;
            }

            using (token)
            {
                using var identity = token is null ? WindowsIdentity.GetCurrent() : new WindowsIdentity(token.DangerousGetHandle());
                return identity.Name;
            }
        }
    }

    public PersonFolders? Folders()
    {
        if (!TryTakeToken(out var token))
        {
            return null;
        }

        using (token)
        {
            var user = token?.DangerousGetHandle() ?? nint.Zero; // zero asks for this process's own user's folders
            return new PersonFolders(
                SHGetKnownFolderPath(FOLDERID_Desktop, KF_FLAG_DONT_VERIFY, user),
                SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, user));
        }
    }

    public async Task<T> RunAsAsync<T>(Func<Task<T>> work)
    {
        if (!TryTakeToken(out var token))
        {
            throw NobodySignedIn();
        }

        if (token is null)
        {
            return await work().ConfigureAwait(false);
        }

        using (token)
        {
            return await WindowsIdentity.RunImpersonatedAsync(token, work).ConfigureAwait(false);
        }
    }

    public async Task<int?> RunHelperAsync(IReadOnlyList<string> arguments, TimeSpan wait)
    {
        if (arguments.Any(argument => argument.Contains('"')))
        {
            throw new ArgumentException("A helper argument cannot contain a quote.", nameof(arguments));
        }

        if (!TryTakeToken(out var token))
        {
            throw NobodySignedIn();
        }

        if (token is null)
        {
            using var own = Process.Start(new ProcessStartInfo(executablePath, arguments) { CreateNoWindow = true })!;
            return await Task.Run(() => own.WaitForExit(wait) ? own.ExitCode : (int?)null).ConfigureAwait(false);
        }

        nint process;
        using (token)
        {
            process = StartAsPerson(token, arguments);
        }

        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: true) };
        return await Task.Run(() => exited.WaitOne(wait) && GetExitCodeProcess(process, out var code) ? (int)code : (int?)null)
            .ConfigureAwait(false);
    }

    /// <summary>Starts the helper with the person's token on their session's desktop and returns its process handle, which the caller closes.</summary>
    private nint StartAsPerson(SafeAccessTokenHandle token, IReadOnlyList<string> arguments)
    {
        // Without the person's own block the helper would inherit the agent's, which is SYSTEM's.
        if (!CreateEnvironmentBlock(out var environment, token.DangerousGetHandle(), false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default", // the session's interactive desktop
            };

            var created = CreateProcessAsUserW(
                token.DangerousGetHandle(),
                executablePath,
                string.Join(' ', arguments.Prepend(executablePath).Select(Quote)),
                nint.Zero,
                nint.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                environment,
                Path.GetDirectoryName(executablePath),
                ref startupInfo,
                out var processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(processInfo.Thread);
            return processInfo.Process;
        }
        finally
        {
            DestroyEnvironmentBlock(environment);
        }
    }

    /// <summary>
    /// True with a fresh token of the person signed in to the agent's session, which the caller disposes; true with
    /// null when the agent is neither SYSTEM nor an elevated administrator and so acts as its own user; false when
    /// nobody is signed in. Throws <see cref="Win32Exception"/> when Windows refuses the token for any other reason,
    /// including to an elevated administrator.
    /// </summary>
    private bool TryTakeToken(out SafeAccessTokenHandle? token)
    {
        token = null;
        if (WTSQueryUserToken(_sessionId, out var handle))
        {
            token = new SafeAccessTokenHandle(handle);
            return true;
        }

        return Marshal.GetLastWin32Error() switch
        {
            ERROR_NO_TOKEN => false,
            ERROR_PRIVILEGE_NOT_HELD when !RunsAsAdministrator() => true,
            var error => throw new Win32Exception(error),
        };
    }

    // RunHelperAsync refuses quotes (no Windows path has one), so only the backslashes before the closing quote need doubling.
    private static string Quote(string argument) =>
        $"\"{argument}{new string('\\', argument.Length - argument.TrimEnd('\\').Length)}\"";

    /// <summary>Whether this process is SYSTEM or an elevated administrator; a non-elevated administrator is not.</summary>
    private static bool RunsAsAdministrator()
    {
        using var self = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(self).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static InvalidOperationException NobodySignedIn() => new("Nobody is signed in to this PC.");

    private static uint OwnSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return (uint)self.SessionId;
    }
}
