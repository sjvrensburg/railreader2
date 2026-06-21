using RailReader.Core;

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
    void NotifyAccessibility();

    /// <summary>Show or hide this surface's focused-pane affordance (a border accent). The VM only
    /// turns it on when more than one surface is live — a lone viewport has no focus ambiguity.</summary>
    void SetFocusedVisual(bool focused);
}
