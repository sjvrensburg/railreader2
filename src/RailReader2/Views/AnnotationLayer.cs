using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class AnnotationLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public MainWindowViewModel? ViewModel { get; set; }

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var tab = Tab;
        var vm = ViewModel;
        if (tab is null || vm is null) return;

        double w = tab.PageWidth > 0 ? tab.PageWidth : Bounds.Width;
        double h = tab.PageHeight > 0 ? tab.PageHeight : Bounds.Height;
        context.Custom(new AnnotationDrawOperation(new Rect(0, 0, w, h), tab, vm));
    }

    private sealed class AnnotationDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel _tab;
        private readonly MainWindowViewModel _vm;

        // Snapshot of mutable state for Equals — when these match, the
        // annotation layer output is identical and Avalonia can skip re-execution.
        private readonly int _page;
        private readonly int _annotationGeneration;
        private readonly Annotation? _selected;
        private readonly Annotation? _preview;
        private readonly List<HighlightRect>? _selectionRects;

        [ThreadStatic] private static SKPaint? s_selPaint;

        public AnnotationDrawOperation(Rect bounds, TabViewModel tab, MainWindowViewModel vm)
        {
            _bounds = bounds;
            _tab = tab;
            _vm = vm;
            _page = tab.CurrentPage;
            _annotationGeneration = tab.State.AnnotationGeneration;
            _selected = vm.SelectedAnnotation;
            _preview = vm.PreviewAnnotation;
            _selectionRects = vm.TextSelectionRects;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is AnnotationDrawOperation op
            && _bounds == op._bounds
            && _page == op._page
            && _annotationGeneration == op._annotationGeneration
            && ReferenceEquals(_selected, op._selected)
            && ReferenceEquals(_preview, op._preview)
            && ReferenceEquals(_selectionRects, op._selectionRects);

        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            if (_tab.Annotations.Pages.TryGetValue(_tab.CurrentPage, out var pageAnnotations))
            {
                AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, _vm.SelectedAnnotation);
            }

            // Draw in-progress annotation preview
            var preview = _vm.PreviewAnnotation;
            if (preview is not null)
                AnnotationRenderer.DrawAnnotation(canvas, preview, false);

            // Draw text selection rects (blue semi-transparent)
            var selRects = _vm.TextSelectionRects;
            if (selRects is { Count: > 0 })
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
    }
}
