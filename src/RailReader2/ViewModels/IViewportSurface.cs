using RailReader.Core;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>
/// A renderable PDF viewport surface — a docked split pane or a floating tear-off window —
/// that <see cref="MainWindowViewModel"/> ticks and invalidates without referencing the concrete
/// view. Implemented by <c>DocumentView</c>; the multi-viewport host (the VM frame loop) drives
/// N of these over one open document, each bound to its own Core <see cref="Viewport"/>.
///
/// <para>The render methods mirror the per-layer invalidation entry points the single-surface
/// path used to reach through <c>InvalidationCallbacks</c>; with multiple surfaces the VM applies
/// each surface's own <c>TickResult</c> to that surface, and broadcasts document-wide invalidations
/// (annotations / search) to every surface.</para>
/// </summary>
public interface IViewportSurface
{
    /// <summary>The Core viewport this surface renders, or <c>null</c> when it has no document yet.</summary>
    Viewport? SurfaceViewport { get; }

    /// <summary>This surface's viewport pixel size. Used to swap the controller's ambient size before
    /// ticking the surface so per-view clamp/snap/centre animate against the correct bounds, and to
    /// keep the viewport's own <see cref="Viewport.SetSize"/> current for <c>HorizontalFraction</c>.</summary>
    (double Width, double Height) SurfaceSize { get; }

    void RenderCamera();
    void RenderPage();
    void RenderOverlay();
    void RenderSearch();
    void RenderAnnotations();
    void RenderPortalMarkers();
    void RenderFreezePanes();
    /// <summary>Re-send this surface's reading-guide layer state (and refresh the pointer-glyph hiding)
    /// for its own viewport's parked / followed guide position.</summary>
    void RenderReadingGuide();
    /// <summary>Re-send just the reading-guide layer's state (and refresh the pointer-glyph hiding) — the
    /// cheap per-frame path for the steer indicator (no line-focus page-state rebuild).</summary>
    void RenderReadingGuideLayer();
    void NotifyAccessibility();

    /// <summary>Dispose <paramref name="images"/> on this surface's composition thread (FIFO after
    /// any state already queued, so a texture is freed only once the layer has stopped drawing it).
    /// Used for a closed tab's page/minimap wraps, which the primary surface was drawing borrowed.</summary>
    void RetireImages(IReadOnlyList<SKImage> images);

    /// <summary>Show or hide this surface's focused-pane affordance (a border accent). The VM only
    /// turns it on when more than one surface is live — a lone viewport has no focus ambiguity.</summary>
    void SetFocusedVisual(bool focused);

    /// <summary>The tab that supplies this surface's model + per-tab display prefs, or null. For a
    /// secondary surface (split pane / tear-off) this is the tab it was created from, independent of
    /// its own viewport.</summary>
    TabViewModel? BoundTab { get; }

    /// <summary>Re-point a secondary surface's model/pref source to a sibling tab of the same document
    /// model — used when the tab it was created from closes while the model (and this surface's own
    /// viewport) survive, so its per-tab prefs don't go stale. No-op for the primary pane.</summary>
    void RebindTab(TabViewModel newTab);
}
