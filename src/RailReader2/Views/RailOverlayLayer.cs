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

public class RailOverlayLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public ColourEffect ActiveEffect { get; set; }
    public bool LineFocusBlurActive { get; set; }
    public bool LineHighlightEnabled { get; set; } = true;
    public double LinePadding { get; set; } = 0.2;
    public LineHighlightTint Tint { get; set; }
    public double TintOpacity { get; set; } = 0.25;

    public RailOverlayLayer()
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
        double w = tab?.PageWidth > 0 ? tab.PageWidth : Bounds.Width;
        double h = tab?.PageHeight > 0 ? tab.PageHeight : Bounds.Height;
        context.Custom(new OverlayDrawOperation(new Rect(0, 0, w, h), tab, ActiveEffect, LineFocusBlurActive, LineHighlightEnabled, LinePadding, Tint, TintOpacity));
    }

    private sealed class OverlayDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffect _effect;
        private readonly bool _lineFocusBlur;
        private readonly bool _lineHighlightEnabled;
        private readonly double _linePadding;
        private readonly LineHighlightTint _tint;
        private readonly double _tintOpacity;

        // Snapshot of dynamic state for Equals — during horizontal scroll
        // block/line indices don't change, so the overlay output is identical.
        private readonly int _currentBlock, _currentLine;
        private readonly bool _railActive, _debugOverlay;

        public OverlayDrawOperation(Rect bounds, TabViewModel? tab, ColourEffect effect, bool lineFocusBlur,
            bool lineHighlightEnabled, double linePadding, LineHighlightTint tint, double tintOpacity)
        {
            _bounds = bounds;
            _tab = tab;
            _effect = effect;
            _lineFocusBlur = lineFocusBlur;
            _lineHighlightEnabled = lineHighlightEnabled;
            _linePadding = linePadding;
            _tint = tint;
            _tintOpacity = tintOpacity;
            _railActive = tab?.Rail.Active == true;
            _currentBlock = tab?.Rail.CurrentBlock ?? 0;
            _currentLine = tab?.Rail.CurrentLine ?? 0;
            _debugOverlay = tab?.DebugOverlay ?? false;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is OverlayDrawOperation op
            && _bounds == op._bounds
            && _effect == op._effect
            && _lineFocusBlur == op._lineFocusBlur
            && _lineHighlightEnabled == op._lineHighlightEnabled
            && _linePadding == op._linePadding
            && _tint == op._tint
            && _tintOpacity == op._tintOpacity
            && _railActive == op._railActive
            && _currentBlock == op._currentBlock
            && _currentLine == op._currentLine
            && _debugOverlay == op._debugOverlay;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var tab = _tab;
            if (tab is null) return;

            if (tab.Rail.Active && tab.Rail.HasAnalysis && tab.Rail.NavigableCount > 0)
            {
                var palette = _effect.GetOverlayPalette();
                OverlayRenderer.DrawRailOverlays(canvas, tab.Rail.CurrentNavigableBlock, tab.Rail.CurrentLineInfo,
                    (float)tab.PageWidth, (float)tab.PageHeight, palette, _lineFocusBlur, _lineHighlightEnabled,
                    _linePadding, _tint, _tintOpacity,
                    OverlayRenderer.GetDimPaint(), OverlayRenderer.GetRevealPaint(),
                    OverlayRenderer.GetOutlinePaint(), OverlayRenderer.GetLinePaint());
            }

            if (tab.DebugOverlay && tab.AnalysisCache.TryGetValue(tab.CurrentPage, out var debugAnalysis))
            {
                OverlayRenderer.DrawDebugOverlay(canvas, debugAnalysis,
                    OverlayRenderer.GetDebugFont(), OverlayRenderer.GetDebugFillPaint(),
                    OverlayRenderer.GetDebugStrokePaint(), OverlayRenderer.GetDebugBgPaint(),
                    OverlayRenderer.GetDebugTextPaint());
            }
        }
    }
}
