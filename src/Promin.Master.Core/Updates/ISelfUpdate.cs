namespace Promin.Master.Core.Updates;

/// <summary>Runs this console's own setup.</summary>
public interface ISelfUpdate
{
    /// <summary>
    /// Starts the console setup at <paramref name="consoleSetupPath"/> elevated, so Windows asks for permission. False
    /// when the permission was refused or the setup could not be started; true once it runs, and this console should close.
    /// </summary>
    bool Start(string consoleSetupPath);
}
