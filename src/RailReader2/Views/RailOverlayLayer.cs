using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.Models;
using RailReader2.Services;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class RailOverlayLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public ColourEffectShaders? ColourEffects { get; set; }

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
        context.Custom(new OverlayDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), Tab, ColourEffects));
    }

    private sealed class OverlayDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;

        public OverlayDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
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

            // Rail overlays — drawn in page coordinates (no camera transform)
            if (tab.Rail.Active && tab.Rail.HasAnalysis)
            {
                var palette = (_effects ?? new ColourEffectShaders()).Effect.GetOverlayPalette();
                DrawRailOverlays(canvas, tab, palette);
            }

            // Debug overlay
            if (tab.DebugOverlay && tab.AnalysisCache.TryGetValue(tab.CurrentPage, out var debugAnalysis))
                DrawDebugOverlay(canvas, tab, debugAnalysis);
        }

        private static void DrawRailOverlays(SKCanvas canvas, TabViewModel tab, OverlayPalette palette)
        {
            if (tab.Rail.NavigableCount == 0) return;
            var block = tab.Rail.CurrentNavigableBlock;
            float margin = 4f;
            var blockRect = SKRect.Create(
                block.BBox.X - margin, block.BBox.Y - margin,
                block.BBox.W + margin * 2, block.BBox.H + margin * 2);

            var pageRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);

            // Dim
            using var dimPaint = new SKPaint { Color = palette.Dim };
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

            // Block reveal
            if (!palette.DimExcludesBlock && palette.BlockReveal is var (revealColor, blendMode))
            {
                canvas.Save();
                canvas.ClipRect(blockRect);
                using var revealPaint = new SKPaint { Color = revealColor, BlendMode = blendMode };
                canvas.DrawRect(blockRect, revealPaint);
                canvas.Restore();
            }

            var bboxRect = BBoxToRect(block.BBox);

            // Block outline
            using var outlinePaint = new SKPaint
            {
                Color = palette.BlockOutline,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = palette.BlockOutlineWidth,
                IsAntialias = true,
            };
            canvas.DrawRect(bboxRect, outlinePaint);

            // Line highlight
            var line = tab.Rail.CurrentLineInfo;
            using var linePaint = new SKPaint { Color = palette.LineHighlight };
            canvas.DrawRect(SKRect.Create(block.BBox.X, line.Y - line.Height / 2, block.BBox.W, line.Height), linePaint);
        }

        private static void DrawDebugOverlay(SKCanvas canvas, TabViewModel tab, PageAnalysis analysis)
        {
            var colors = new SKColor[] {
                new(244, 67, 54), new(33, 150, 243), new(76, 175, 80),
                new(255, 152, 0), new(156, 39, 176), new(0, 188, 212) };

            using var font = new SKFont(SKTypeface.Default, 8);

            foreach (var block in analysis.Blocks)
            {
                var color = colors[block.ClassId % colors.Length];
                var rect = BBoxToRect(block.BBox);

                using var rectPaint = new SKPaint { Color = color.WithAlpha(50) };
                canvas.DrawRect(rect, rectPaint);

                using var strokePaint = new SKPaint
                {
                    Color = color.WithAlpha(180),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1,
                };
                canvas.DrawRect(rect, strokePaint);

                string className = block.ClassId < LayoutConstants.LayoutClasses.Length
                    ? LayoutConstants.LayoutClasses[block.ClassId] : "unknown";
                string label = $"#{block.Order} {className} ({block.Confidence * 100:F0}%)";

                using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 200) };
                canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y - 10, label.Length * 5f, 11), bgPaint);

                using var textPaint = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawText(label, block.BBox.X + 1, block.BBox.Y - 1, font, textPaint);
            }
        }

        private static SKRect BBoxToRect(BBox bbox) =>
            SKRect.Create(bbox.X, bbox.Y, bbox.W, bbox.H);
    }
}
