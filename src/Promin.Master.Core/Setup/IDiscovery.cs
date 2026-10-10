using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Setup;

/// <summary>LAN discovery, as the setup screen needs it.</summary>
public interface IDiscovery
{
    /// <summary>Raised on any thread each time an agent PC announces itself.</summary>
    event Action<AgentPc>? Seen;

    /// <summary>
    /// Starts listening. Returns false when it cannot — the discovery port is already in use on
    /// this PC — so that the screen can say so instead of looking for ever.
    /// </summary>
    bool Start();

    void Stop();
}
