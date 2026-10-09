namespace Educast.Master.Core.Wall;

/// <summary>One count pill in the wall's header; its dot takes the colour of <paramref name="State"/>.</summary>
public sealed record WallPill(PcTileState State, string Text);
