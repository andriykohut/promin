namespace Promin.Master.Core.Updates;

/// <summary>Runs this console's own setup.</summary>
public interface ISelfUpdate
{
    /// <summary>
    /// Runs the console setup at <paramref name="consoleSetupPath"/>, which asks Windows for permission itself and closes
    /// this console before it installs. Completes only while this console is still open: when the permission was
    /// refused, or the setup failed before it installed or could not be started.
    /// </summary>
    Task RunAsync(string consoleSetupPath);
}
