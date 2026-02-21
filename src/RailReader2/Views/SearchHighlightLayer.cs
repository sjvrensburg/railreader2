using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.Models;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class SearchHighlightLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public MainWindowViewModel? ViewModel { get; set; }

    public SearchHighlightLayer()
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
        context.Custom(new SearchDrawOperation(new Rect(0, 0, w, h), tab, vm));
    }

    private sealed class SearchDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel _tab;
        private readonly MainWindowViewModel _vm;

        public SearchDrawOperation(Rect bounds, TabViewModel tab, MainWindowViewModel vm)
        {
            _bounds = bounds;
            _tab = tab;
            _vm = vm;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var matches = _vm.CurrentPageSearchMatches;
            if (matches is null || matches.Count == 0) return;

            int activeGlobal = _vm.ActiveMatchIndex;
            int pageIndex = _tab.CurrentPage;

            using var highlightPaint = new SKPaint { Color = new SKColor(255, 255, 0, 100), IsAntialias = true };
            using var activePaint = new SKPaint { Color = new SKColor(255, 165, 0, 160), IsAntialias = true };

            // Determine which match in the current page list is the active one
            int activeLocalIndex = -1;
            if (activeGlobal >= 0 && activeGlobal < _vm.SearchMatches.Count)
            {
                var activeMatch = _vm.SearchMatches[activeGlobal];
                if (activeMatch.PageIndex == pageIndex)
                {
                    activeLocalIndex = matches.IndexOf(activeMatch);
                }
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var paint = i == activeLocalIndex ? activePaint : highlightPaint;
                foreach (var rect in matches[i].Rects)
                {
                    canvas.DrawRect(rect, paint);
                }
            }
        }
    }
}
