using System.ComponentModel;
using System.Diagnostics;
using Promin.Master.Core.Updates;

namespace Promin.Master.Services;

/// <summary>
/// Runs the console's setup as this console runs, without elevation. The setup asks Windows for permission itself and
/// keeps a part that is not elevated, which opens the console again as the person who clicked Update: not elevated, and
/// not as an administrator who typed their own password. Told by <c>/PROMINUPDATE</c> that this console ran it, the
/// setup closes this console before it installs and opens it again with <c>--continue-update</c> (see
/// <c>installer/console.iss</c>). It shows its progress but asks nothing.
/// </summary>
public sealed class SelfUpdate : ISelfUpdate
{
    private const string Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /PROMINUPDATE";

    public async Task RunAsync(string consoleSetupPath)
    {
        try
        {
            using var setup = Process.Start(new ProcessStartInfo(consoleSetupPath, Arguments) { UseShellExecute = false })!;
            await setup.WaitForExitAsync().ConfigureAwait(false);
            Trace.TraceInformation("This PC's setup ended with {0} and did not update it.", setup.ExitCode);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("This PC's setup could not be started: {0}", e.Message);
        }
    }
}
