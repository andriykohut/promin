namespace Promin.Agent;

/// <summary>The signed-in person's Desktop and LocalAppData folders, as Windows reports them.</summary>
internal sealed record PersonFolders(string Desktop, string LocalAppData);

/// <summary>
/// Everything the agent does as the person signed in to its session: received files are theirs to edit and delete,
/// and what opens on their screen opens as them, never as SYSTEM.
/// </summary>
internal interface ISignedInPerson
{
    /// <summary>DOMAIN\name of the person signed in to this agent's session; null when nobody is.</summary>
    string? Name { get; }

    /// <summary>
    /// Their Desktop and LocalAppData as Windows reports them; null when nobody is signed in. Throws when Windows
    /// cannot give them.
    /// </summary>
    PersonFolders? Folders();

    /// <summary>
    /// Runs file work inside the person's identity (or as this process, when the agent is not SYSTEM). Throws
    /// <see cref="InvalidOperationException"/> when nobody is signed in.
    /// </summary>
    Task<T> RunAsAsync<T>(Func<Task<T>> work);

    /// <summary>
    /// Starts Promin.Agent.exe with these arguments as the person, without administrator rights and without a window.
    /// Returns its exit code if it ended within <paramref name="wait"/>, otherwise null; a helper still running is
    /// left to finish. Throws <see cref="System.ComponentModel.Win32Exception"/> when it cannot start, and
    /// <see cref="InvalidOperationException"/> when nobody is signed in.
    /// </summary>
    Task<int?> RunHelperAsync(IReadOnlyList<string> arguments, TimeSpan wait);
}
