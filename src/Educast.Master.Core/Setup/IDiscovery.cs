using Educast.Master.Core.Rooms;

namespace Educast.Master.Core.Setup;

/// <summary>LAN discovery, as the setup screen needs it.</summary>
public interface IDiscovery
{
    /// <summary>Raised on any thread each time a student PC announces itself.</summary>
    event Action<StudentPc>? Seen;

    void Start();

    void Stop();
}
