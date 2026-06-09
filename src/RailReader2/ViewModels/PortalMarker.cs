using RailReader2.Services;

namespace RailReader2.ViewModels;

public enum PortalMarkerKind { Source, Target }

/// <summary>
/// A drawable/clickable portal indicator on the current page: a gutter marker beside a source line,
/// or a corner badge on a target block. Groups every portal that shares the anchor — a source line
/// can link several targets, and a target can be linked from several sources — so a click on a marker
/// representing more than one portal opens a chooser. Positions are page-space; the layer maps them to
/// fixed-size screen glyphs.
/// </summary>
public sealed class PortalMarker
{
    public required PortalMarkerKind Kind { get; init; }
    public required double PageX { get; init; }
    public required double PageY { get; init; }
    public required IReadOnlyList<Portal> Portals { get; init; }
    /// <summary>True when one of this marker's portals is the currently-pinned one (drawn accented).</summary>
    public required bool IsActive { get; init; }
    public int Count => Portals.Count;
}
