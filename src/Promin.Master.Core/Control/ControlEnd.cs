namespace Promin.Master.Core.Control;

/// <summary>How a remote session, viewing or controlling, ended.</summary>
public enum ControlEnd
{
    /// <summary>Stopped by the person, or ended for a lock, a removal or another PC.</summary>
    Closed,

    /// <summary>The stream or the connection went.</summary>
    Lost,

    /// <summary>The agent PC refused to be controlled after it was viewed.</summary>
    Refused,
}
