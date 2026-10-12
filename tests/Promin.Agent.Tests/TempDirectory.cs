namespace Promin.Agent.Tests;

/// <summary>
/// A folder under the temp directory that is deleted afterwards. Nothing in these tests touches the real
/// ProgramData or LocalAppData: every path the agent uses is passed in.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"promin-agent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Paths laid out as on an agent PC, under this folder.</summary>
    public AgentPaths Paths => new(
        System.IO.Path.Combine(Path, "ProgramData", "Promin", "Agent"),
        System.IO.Path.Combine(Path, "ProgramData", "Promin", "agent.json"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
