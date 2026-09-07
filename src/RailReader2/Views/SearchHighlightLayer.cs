using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// One visible page's own search matches + camera. In single-page mode there is always exactly one
/// entry. The active-match local index is pre-computed on the UI thread to keep the composition
/// thread free of search-list traversal.
/// </summary>
internal readonly record struct SearchPageState(
    SKMatrix Camera, IReadOnlyList<SearchMatch>? Matches, int ActiveLocalIndex, SKRect ViewportInPageSpace);

/// <summary>
/// Immutable snapshot of all state needed to render search highlights for one frame.
/// </summary>
internal sealed record SearchRenderState(IReadOnlyList<SearchPageState> Pages);

/// <summary>
/// Hosts a CompositionCustomVisual for search highlight rendering.
/// Camera transform applied inside Skia; match rects are in page space.
/// </summary>
internal class SearchHighlightLayer : CompositionLayerControl<SearchVisualHandler>;

internal sealed class SearchVisualHandler : CompositionCustomVisualHandler
{
    private SearchRenderState? _state;

    public override void OnMessage(object message)
    {
        if (message is SearchRenderState state)
        {
            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null || state.Pages.Count == 0) return;

        bool hasAnyMatches = false;
        foreach (var p in state.Pages)
            if (p.Matches is { Count: > 0 }) { hasAnyMatches = true; break; }
        if (!hasAnyMatches) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        foreach (var page in state.Pages)
        {
            if (page.Matches is not { Count: > 0 } matches) continue;

            canvas.Save();
            canvas.Concat(page.Camera);

            OverlayRenderer.DrawSearchHighlights(canvas, matches, page.ActiveLocalIndex,
                OverlayRenderer.GetHighlightPaint(), OverlayRenderer.GetActivePaint(),
                page.ViewportInPageSpace);

            canvas.Restore();
        }
    }
}
