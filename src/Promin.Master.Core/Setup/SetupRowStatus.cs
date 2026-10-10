namespace Promin.Master.Core.Setup;

/// <summary>Where one student PC stands on the setup screen.</summary>
public enum SetupRowStatus
{
    NotPaired,
    Waiting,
    Pairing,
    InRoom,
    WrongCode,
    Unreachable,
    CannotPairNow,
}
