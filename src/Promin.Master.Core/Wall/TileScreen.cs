namespace Promin.Master.Core.Wall;

/// <summary>What a tile's screen area shows over its state's background.</summary>
public enum TileScreen
{
    /// <summary>The state's glyph: no picture yet, offline, reconnecting.</summary>
    Glyph,

    /// <summary>The PC's screen, fitted whole.</summary>
    Picture,

    /// <summary>The PC's screen cannot be captured right now: a lock glyph and "Screen not available".</summary>
    NotAvailable,
}
