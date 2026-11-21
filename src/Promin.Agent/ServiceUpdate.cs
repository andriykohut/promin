using System.Diagnostics;
using Promin.Protocol.Updates;

namespace Promin.Agent;

/// <summary>
/// The service's half of an update: the agent process saved and checked a setup and exited with
/// <see cref="ExitCodes.UpdateReady"/>; the service checks it again before it runs anything as SYSTEM, then starts it in
/// session 0, where no window it might open can be reached by the person at the PC.
/// </summary>
internal static class ServiceUpdate
{
    private const string SetupArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    /// <summary>
    /// True when the setup in <paramref name="updateDirectory"/> is newer than <paramref name="installed"/>, its
    /// <c>.sig</c> holds for <paramref name="publicKey"/>, and <paramref name="startSetup"/> started it. When the check
    /// fails, the folder is emptied and false returned. Never throws.
    /// </summary>
    public static bool TryStart(
        string updateDirectory, ReleaseVersion installed, ReadOnlySpan<byte> publicKey, Func<string, bool> startSetup, Action<string> log)
    {
        var setup = Path.Combine(updateDirectory, UpdateLink.SetupName);
        var sig = SetupSignature.Read(setup + SetupSignature.Extension);
        if (sig is null
            || !ReleaseVersion.TryParse(sig.Version, out var version)
            || version <= installed
            || !UpdateSignature.HoldsFor(setup, version, sig.Signature, publicKey))
        {
            log("The update the agent left is not a newer setup signed by the project; it was removed.");
            UpdateLink.ClearLeftovers(updateDirectory, log);
            return false;
        }

        if (!startSetup(setup))
        {
            log($"The setup for {version} could not be started.");
            return false;
        }

        log($"The setup for {version} is running.");
        return true;
    }

    /// <summary>Starts the agent setup unattended; false when it could not be started.</summary>
    public static bool StartSetup(string setupPath)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(setupPath, SetupArguments) { UseShellExecute = false });
            return started is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
