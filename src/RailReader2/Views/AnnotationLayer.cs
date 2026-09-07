using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// One visible page's own annotation set + camera. In single-page mode there is always exactly one
/// entry (<see cref="IsAnchor"/> true).
/// </summary>
internal readonly record struct AnnotationPageState(
    int Page, bool IsAnchor, SKMatrix Camera, List<Annotation>? Annotations);

/// <summary>
/// Immutable snapshot of all state needed to render annotations for one frame. Preview annotation
/// (mid-authoring) and text-selection rects are always on the anchor page — annotation authoring
/// stays page-local and anchored per the continuous-scroll host contract.
/// </summary>
internal sealed record AnnotationRenderState(
    IReadOnlyList<AnnotationPageState> Pages,
    Annotation? SelectedAnnotation,
    Annotation? PreviewAnnotation,
    List<HighlightRect>? TextSelectionRects);

/// <summary>
/// Hosts a CompositionCustomVisual for annotation rendering.
/// Camera transform applied inside Skia; annotations are in page space.
/// </summary>
internal class AnnotationLayer : CompositionLayerControl<AnnotationVisualHandler>;

internal sealed class AnnotationVisualHandler : CompositionCustomVisualHandler
{
    private AnnotationRenderState? _state;

    [ThreadStatic] private static SKPaint? s_selPaint;

    public override void OnMessage(object message)
    {
        if (message is AnnotationRenderState state)
        {
            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null) return;

        bool hasAnyAnnotations = false;
        foreach (var p in state.Pages)
            if (p.Annotations is { Count: > 0 }) { hasAnyAnnotations = true; break; }
        bool hasContent = hasAnyAnnotations
            || state.PreviewAnnotation is not null
            || state.TextSelectionRects is { Count: > 0 };
        if (!hasContent) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        foreach (var page in state.Pages)
        {
            bool hasPageContent = page.Annotations is { Count: > 0 }
                || (page.IsAnchor && (state.PreviewAnnotation is not null || state.TextSelectionRects is { Count: > 0 }));
            if (!hasPageContent) continue;

            canvas.Save();
            canvas.Concat(page.Camera);

            if (page.Annotations is { } annotations)
                AnnotationRenderer.DrawAnnotations(canvas, annotations, state.SelectedAnnotation);

            if (page.IsAnchor)
            {
                if (state.PreviewAnnotation is { } preview)
                    AnnotationRenderer.DrawPreviewAnnotation(canvas, preview);

                if (state.TextSelectionRects is { Count: > 0 } selRects)
                {
                    var selPaint = s_selPaint ??= new SKPaint
                    {
                        Color = new SKColor(0x33, 0x90, 0xFF, 77),
                        IsAntialias = true,
                    };
                    foreach (var r in selRects)
                        canvas.DrawRect(SKRect.Create(r.X, r.Y, r.W, r.H), selPaint);
                }
            }

            canvas.Restore();
        }
    }
}
