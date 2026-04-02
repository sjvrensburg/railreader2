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

        // Cached paints — one set per render thread to avoid cross-thread mutation.
        // Reused every frame; only Color/BlendMode/StrokeWidth are mutated between draws.
        [ThreadStatic] private static SKPaint? s_dimPaint;
        [ThreadStatic] private static SKPaint? s_revealPaint;
        [ThreadStatic] private static SKPaint? s_outlinePaint;
        [ThreadStatic] private static SKPaint? s_linePaint;
        [ThreadStatic] private static SKPaint? s_debugFillPaint;
        [ThreadStatic] private static SKPaint? s_debugStrokePaint;
        [ThreadStatic] private static SKPaint? s_debugBgPaint;
        [ThreadStatic] private static SKPaint? s_debugTextPaint;
        [ThreadStatic] private static SKFont? s_debugFont;

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
                var dimPaint = s_dimPaint ??= new SKPaint();
                var revealPaint = s_revealPaint ??= new SKPaint();
                var outlinePaint = s_outlinePaint ??= new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
                var linePaint = s_linePaint ??= new SKPaint();
                OverlayRenderer.DrawRailOverlays(canvas, tab.Rail.CurrentNavigableBlock, tab.Rail.CurrentLineInfo,
                    (float)tab.PageWidth, (float)tab.PageHeight, palette, _lineFocusBlur, _lineHighlightEnabled,
                    _linePadding, _tint, _tintOpacity, dimPaint, revealPaint, outlinePaint, linePaint);
            }

            if (tab.DebugOverlay && tab.AnalysisCache.TryGetValue(tab.CurrentPage, out var debugAnalysis))
            {
                var font = s_debugFont ??= new SKFont(SKTypeface.Default, 8);
                var fillPaint = s_debugFillPaint ??= new SKPaint();
                var strokePaint = s_debugStrokePaint ??= new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                var bgPaint = s_debugBgPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 200) };
                var textPaint = s_debugTextPaint ??= new SKPaint { IsAntialias = true };
                OverlayRenderer.DrawDebugOverlay(canvas, debugAnalysis, font, fillPaint, strokePaint, bgPaint, textPaint);
            }
        }
    }
}
