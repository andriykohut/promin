namespace Promin.Master.Core.Setup;

/// <summary>What one attempt to pair a student PC came to, in the terms the screen uses.</summary>
public enum PairResult
{
    Paired,
    WrongCode,
    Unreachable,
    CannotPairNow,
}
