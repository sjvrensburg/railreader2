using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable snapshot of all state needed to render search highlights for one frame.
/// The active-match local index is pre-computed on the UI thread to keep the
/// composition thread free of search-list traversal.
/// </summary>
internal sealed record SearchRenderState(
    SKMatrix Camera,
    List<SearchMatch>? Matches,
    int ActiveLocalIndex);

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
        if (state?.Matches is not { Count: > 0 } matches) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();
        canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, state.Camera));

        OverlayRenderer.DrawSearchHighlights(canvas, matches, state.ActiveLocalIndex,
            OverlayRenderer.GetHighlightPaint(), OverlayRenderer.GetActivePaint());

        canvas.Restore();
    }
}
