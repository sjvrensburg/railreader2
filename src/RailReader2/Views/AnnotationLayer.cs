using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Media;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable snapshot of all state needed to render annotations for one frame.
/// </summary>
internal sealed record AnnotationRenderState(
    SKMatrix Camera,
    List<Annotation>? PageAnnotations,
    Annotation? SelectedAnnotation,
    Annotation? PreviewAnnotation,
    List<HighlightRect>? TextSelectionRects);

/// <summary>
/// Hosts a CompositionCustomVisual for annotation rendering.
/// Camera transform applied inside Skia; annotations are in page space.
/// </summary>
public class AnnotationLayer : Control
{
    private CompositionCustomVisual? _visual;
    private readonly AnnotationVisualHandler _handler = new();

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor is not null)
        {
            _visual = compositor.CreateCustomVisual(_handler);
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual is not null)
            _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    internal void UpdateState(AnnotationRenderState state) =>
        _visual?.SendHandlerMessage(state);
}

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

        bool hasContent = state.PageAnnotations is { Count: > 0 }
            || state.PreviewAnnotation is not null
            || state.TextSelectionRects is { Count: > 0 };
        if (!hasContent) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();
        canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, state.Camera));

        if (state.PageAnnotations is { } annotations)
            AnnotationRenderer.DrawAnnotations(canvas, annotations, state.SelectedAnnotation);

        if (state.PreviewAnnotation is { } preview)
            AnnotationRenderer.DrawAnnotation(canvas, preview, false);

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

        canvas.Restore();
    }
}
