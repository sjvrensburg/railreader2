using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.Models;
using RailReader2.Services;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class PdfCanvasControl : Control
{
    public MainWindowViewModel? ViewModel { get; set; }
    private bool _dragging;
    private Point _lastPos;
    private Point _pressPos;

    public PdfCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new PdfDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), ViewModel));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ViewModel is null) return;

        double scrollY = e.Delta.Y * 30.0;
        var pos = e.GetPosition(this);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        ViewModel.HandleZoom(scrollY, pos.X, pos.Y, ctrl);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _pressPos = e.GetPosition(this);
            _lastPos = _pressPos;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && ViewModel is not null)
        {
            var pos = e.GetPosition(this);
            double dx = pos.X - _lastPos.X;
            double dy = pos.Y - _lastPos.Y;
            ViewModel.HandlePan(dx, dy);
            _lastPos = pos;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging && ViewModel is not null)
        {
            var pos = e.GetPosition(this);
            double dist = Math.Sqrt(Math.Pow(pos.X - _pressPos.X, 2) + Math.Pow(pos.Y - _pressPos.Y, 2));
            if (dist < 5.0)
                ViewModel.HandleClick(pos.X, pos.Y);
        }
        _dragging = false;
        e.Handled = true;
    }

    private sealed class PdfDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly MainWindowViewModel? _vm;

        public PdfDrawOperation(Rect bounds, MainWindowViewModel? vm)
        {
            _bounds = bounds;
            _vm = vm;
        }

        public Rect Bounds => _bounds;

        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            // Clear background
            canvas.Clear(new SKColor(128, 128, 128));

            var tab = _vm?.ActiveTab;
            if (tab?.CachedBitmap is not { } bitmap) return;

            canvas.Save();
            canvas.Translate((float)tab.Camera.OffsetX, (float)tab.Camera.OffsetY);
            canvas.Scale((float)tab.Camera.Zoom, (float)tab.Camera.Zoom);

            // Draw colour effect layer
            var effectPaint = _vm?.ColourEffects.CreatePaint();
            if (effectPaint is not null)
            {
                canvas.SaveLayer(effectPaint);
            }

            // Draw page bitmap scaled to page dimensions (points)
            // Use bilinear+mipmap filtering for smooth rendering at any zoom
            var destRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);
            using var image = SKImage.FromBitmap(bitmap);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            canvas.DrawImage(image, destRect, sampling);

            if (effectPaint is not null)
            {
                canvas.Restore(); // pop save layer
                effectPaint.Dispose();
            }

            // Rail overlays
            if (tab.Rail.Active && tab.Rail.HasAnalysis)
            {
                var palette = _vm!.ColourEffects.Effect.GetOverlayPalette();
                DrawRailOverlays(canvas, tab, palette);
            }

            // Debug overlay — use analysis cache directly so it works before rail activates
            if (tab.DebugOverlay && tab.AnalysisCache.TryGetValue(tab.CurrentPage, out var debugAnalysis))
                DrawDebugOverlay(canvas, tab, debugAnalysis);

            canvas.Restore();
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

            // Block outline
            using var outlinePaint = new SKPaint
            {
                Color = palette.BlockOutline,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = palette.BlockOutlineWidth,
                IsAntialias = true,
            };
            canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H), outlinePaint);

            // Line highlight
            var line = tab.Rail.CurrentLineInfo;
            using var linePaint = new SKPaint { Color = palette.LineHighlight };
            canvas.DrawRect(SKRect.Create(block.BBox.X, line.Y - line.Height / 2, block.BBox.W, line.Height), linePaint);
        }

        private static void DrawDebugOverlay(SKCanvas canvas, TabViewModel tab, PageAnalysis analysis)
        {
            var colors = new[] {
                (244, 67, 54), (33, 150, 243), (76, 175, 80),
                (255, 152, 0), (156, 39, 176), (0, 188, 212) };

            using var font = new SKFont(SKTypeface.Default, 8);

            foreach (var block in analysis.Blocks)
            {
                var (cr, cg, cb) = colors[block.ClassId % colors.Length];

                using var rectPaint = new SKPaint { Color = new SKColor((byte)cr, (byte)cg, (byte)cb, 50) };
                canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H), rectPaint);

                using var strokePaint = new SKPaint
                {
                    Color = new SKColor((byte)cr, (byte)cg, (byte)cb, 180),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1,
                };
                canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H), strokePaint);

                string className = block.ClassId < LayoutConstants.LayoutClasses.Length
                    ? LayoutConstants.LayoutClasses[block.ClassId] : "unknown";
                string label = $"#{block.Order} {className} ({block.Confidence * 100:F0}%)";

                using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 200) };
                canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y - 10, label.Length * 5f, 11), bgPaint);

                using var textPaint = new SKPaint
                {
                    Color = new SKColor((byte)cr, (byte)cg, (byte)cb),
                    IsAntialias = true,
                };
                canvas.DrawText(label, block.BBox.X + 1, block.BBox.Y - 1, font, textPaint);
            }
        }
    }
}
