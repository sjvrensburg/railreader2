using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Core.Services;
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
        private readonly List<SearchMatch>? _matches;
        private readonly int _activeMatchIndex;
        private readonly int _currentPage;

        [ThreadStatic] private static SKPaint? s_highlightPaint;
        [ThreadStatic] private static SKPaint? s_activePaint;

        public SearchDrawOperation(Rect bounds, TabViewModel tab, MainWindowViewModel vm)
        {
            _bounds = bounds;
            _tab = tab;
            _vm = vm;
            _matches = vm.CurrentPageSearchMatches;
            _activeMatchIndex = vm.ActiveMatchIndex;
            _currentPage = tab.CurrentPage;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is SearchDrawOperation op
            && _bounds == op._bounds
            && ReferenceEquals(_matches, op._matches)
            && _activeMatchIndex == op._activeMatchIndex
            && _currentPage == op._currentPage;

        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var matches = _matches;
            if (matches is null || matches.Count == 0) return;

            var highlightPaint = s_highlightPaint ??= new SKPaint { Color = new SKColor(255, 255, 0, 100), IsAntialias = true };
            var activePaint = s_activePaint ??= new SKPaint { Color = new SKColor(255, 165, 0, 160), IsAntialias = true };

            int activeLocalIndex = OverlayRenderer.ComputeActiveLocalIndex(
                _vm.SearchMatches, matches, _activeMatchIndex, _currentPage);
            OverlayRenderer.DrawSearchHighlights(canvas, matches, activeLocalIndex, highlightPaint, activePaint);
        }
    }
}
