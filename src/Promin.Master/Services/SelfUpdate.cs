using System.ComponentModel;
using System.Diagnostics;
using Promin.Master.Core.Updates;

namespace Promin.Master.Services;

/// <summary>
/// Runs the console's setup elevated, so Windows asks for permission. It shows its progress but asks nothing, closes
/// this console through Restart Manager if it is still open, and, told by <c>/PROMINUPDATE</c> that this console ran it,
/// opens it again with <c>--continue-update</c> (see <c>installer/console.iss</c>).
/// </summary>
public sealed class SelfUpdate : ISelfUpdate
{
    private const string Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /PROMINUPDATE";

    // ERROR_CANCELLED: the person said no to Windows' permission prompt.
    private const int Cancelled = 1223;

    public bool Start(string consoleSetupPath)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(consoleSetupPath, Arguments) { UseShellExecute = true, Verb = "runas" });
            return started is not null;
        }
        catch (Win32Exception e)
        {
            if (e.NativeErrorCode != Cancelled)
            {
                Trace.TraceWarning("This PC's setup could not be started: {0}", e.Message);
            }

            return false;
        }
    }
}
