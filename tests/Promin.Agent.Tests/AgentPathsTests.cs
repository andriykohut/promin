using Promin.Agent;

namespace Promin.Agent.Tests;

public class AgentPathsTests
{
    [Fact]
    public void ForThisMachine_KeepsEverythingUnderProgramDataPromin()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var paths = AgentPaths.ForThisMachine();

        Assert.Equal(Path.Combine(programData, "Promin", "Agent"), paths.AgentDirectory);
        Assert.Equal(Path.Combine(programData, "Promin", "agent.json"), paths.RoomCodeFile);
    }
}
