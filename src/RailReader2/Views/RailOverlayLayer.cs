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
    public ColourEffectShaders? ColourEffects { get; set; }
    public bool LineFocusBlurActive { get; set; }

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
        context.Custom(new OverlayDrawOperation(new Rect(0, 0, w, h), tab, ColourEffects, LineFocusBlurActive));
    }

    private sealed class OverlayDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;
        private readonly bool _lineFocusBlur;

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

        public OverlayDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects, bool lineFocusBlur)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
            _lineFocusBlur = lineFocusBlur;
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

            if (tab.Rail.Active && tab.Rail.HasAnalysis)
            {
                var effect = _effects?.Effect ?? ColourEffect.None;
                DrawRailOverlays(canvas, tab, effect.GetOverlayPalette(), _lineFocusBlur);
            }

            if (tab.DebugOverlay && tab.AnalysisCache.TryGetValue(tab.CurrentPage, out var debugAnalysis))
                DrawDebugOverlay(canvas, tab, debugAnalysis);
        }

        private static void DrawRailOverlays(SKCanvas canvas, TabViewModel tab, OverlayPalette palette, bool lineFocusBlur)
        {
            if (tab.Rail.NavigableCount == 0) return;
            var block = tab.Rail.CurrentNavigableBlock;
            float margin = 4f;
            var blockRect = SKRect.Create(
                block.BBox.X - margin, block.BBox.Y - margin,
                block.BBox.W + margin * 2, block.BBox.H + margin * 2);

            var pageRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);

            var dimPaint = s_dimPaint ??= new SKPaint();
            dimPaint.Color = palette.Dim;
            if (palette.DimExcludesBlock)
            {
                canvas.Save();
                canvas.ClipRect(blockRect, SKClipOperation.Difference);
                canvas.DrawRect(pageRect, dimPaint);
                canvas.Restore();
            }
            else
            {
                canvas.DrawRect(pageRect, dimPaint);
            }

            if (!palette.DimExcludesBlock && palette.BlockReveal is var (revealColor, blendMode))
            {
                canvas.Save();
                canvas.ClipRect(blockRect);
                var revealPaint = s_revealPaint ??= new SKPaint();
                revealPaint.Color = revealColor;
                revealPaint.BlendMode = blendMode;
                canvas.DrawRect(blockRect, revealPaint);
                canvas.Restore();
            }

            var bboxRect = BBoxToRect(block.BBox);

            var outlinePaint = s_outlinePaint ??= new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            outlinePaint.Color = palette.BlockOutline;
            outlinePaint.StrokeWidth = palette.BlockOutlineWidth;
            canvas.DrawRect(bboxRect, outlinePaint);

            var line = tab.Rail.CurrentLineInfo;
            var linePaint = s_linePaint ??= new SKPaint();

            if (lineFocusBlur)
            {
                // When line focus blur is active, the blur itself highlights the
                // active line. Draw a narrow indicator bar on the left edge instead
                // of the full-width tinted highlight.
                const float barWidth = 3f;
                float pad = line.Height * 0.15f;
                linePaint.Color = palette.BlockOutline.WithAlpha(200);
                canvas.DrawRect(SKRect.Create(
                    block.BBox.X - margin - barWidth,
                    line.Y - line.Height / 2 - pad,
                    barWidth,
                    line.Height + pad * 2), linePaint);
            }
            else
            {
                linePaint.Color = palette.LineHighlight;
                canvas.DrawRect(SKRect.Create(block.BBox.X, line.Y - line.Height / 2, block.BBox.W, line.Height), linePaint);
            }
        }

        private static readonly SKColor[] s_debugColors =
        [
            new(244, 67, 54), new(33, 150, 243), new(76, 175, 80),
            new(255, 152, 0), new(156, 39, 176), new(0, 188, 212),
        ];

        private static void DrawDebugOverlay(SKCanvas canvas, TabViewModel tab, PageAnalysis analysis)
        {
            var font = s_debugFont ??= new SKFont(SKTypeface.Default, 8);
            var fillPaint = s_debugFillPaint ??= new SKPaint();
            var strokePaint = s_debugStrokePaint ??= new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            var bgPaint = s_debugBgPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 200) };
            var textPaint = s_debugTextPaint ??= new SKPaint { IsAntialias = true };

            foreach (var block in analysis.Blocks)
            {
                var color = s_debugColors[block.ClassId % s_debugColors.Length];
                var rect = BBoxToRect(block.BBox);

                fillPaint.Color = color.WithAlpha(50);
                canvas.DrawRect(rect, fillPaint);

                strokePaint.Color = color.WithAlpha(180);
                canvas.DrawRect(rect, strokePaint);

                string className = block.ClassId < LayoutConstants.LayoutClasses.Length
                    ? LayoutConstants.LayoutClasses[block.ClassId] : "unknown";
                string label = $"#{block.Order} {className} ({block.Confidence * 100:F0}%)";

                canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y - 10, label.Length * 5f, 11), bgPaint);

                textPaint.Color = color;
                canvas.DrawText(label, block.BBox.X + 1, block.BBox.Y - 1, font, textPaint);
            }
        }

        private static SKRect BBoxToRect(BBox bbox) =>
            SKRect.Create(bbox.X, bbox.Y, bbox.W, bbox.H);
    }
}
