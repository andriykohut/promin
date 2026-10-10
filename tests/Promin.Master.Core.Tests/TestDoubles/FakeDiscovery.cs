using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;

namespace Promin.Master.Core.Tests.TestDoubles;

public sealed class FakeDiscovery : IDiscovery
{
    public event Action<AgentPc>? Seen;

    public int Started { get; private set; }

    public int Stopped { get; private set; }

    /// <summary>False makes <see cref="Start"/> report that the port could not be bound.</summary>
    public bool CanStart { get; set; } = true;

    public bool Start()
    {
        Started++;
        return CanStart;
    }

    public void Stop() => Stopped++;

    public void Raise(AgentPc pc) => Seen?.Invoke(pc);
}
