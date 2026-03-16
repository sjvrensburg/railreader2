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

public class RailOverlayLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public ColourEffect ActiveEffect { get; set; }
    public bool LineFocusBlurActive { get; set; }
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
        context.Custom(new OverlayDrawOperation(new Rect(0, 0, w, h), tab, ActiveEffect, LineFocusBlurActive, Tint, TintOpacity));
    }

    private sealed class OverlayDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffect _effect;
        private readonly bool _lineFocusBlur;
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

        public OverlayDrawOperation(Rect bounds, TabViewModel? tab, ColourEffect effect, bool lineFocusBlur,
            LineHighlightTint tint, double tintOpacity)
        {
            _bounds = bounds;
            _tab = tab;
            _effect = effect;
            _lineFocusBlur = lineFocusBlur;
            _tint = tint;
            _tintOpacity = tintOpacity;
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
                    (float)tab.PageWidth, (float)tab.PageHeight, palette, _lineFocusBlur, _tint, _tintOpacity,
                    dimPaint, revealPaint, outlinePaint, linePaint);
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
