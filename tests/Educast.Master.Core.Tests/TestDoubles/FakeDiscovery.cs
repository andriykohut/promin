using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Tests.TestDoubles;

public sealed class FakeDiscovery : IDiscovery
{
    public event Action<StudentPc>? Seen;

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

    public void Raise(StudentPc pc) => Seen?.Invoke(pc);
}
