namespace Educast.Master.Core.Wall;

/// <summary>One count pill in the wall's header: a count, its word, and the state its dot is coloured by.</summary>
public sealed record WallPill(PcTileState State, int Count, string Word);
