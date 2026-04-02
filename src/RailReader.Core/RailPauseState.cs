namespace RailReader.Core;

/// <summary>
/// Immutable snapshot of rail navigation state captured when the user
/// initiates a Ctrl+drag free pan. Restored when Ctrl is released.
/// </summary>
internal sealed record RailPauseState(int Block, int Line, double VerticalBias, double Zoom);
