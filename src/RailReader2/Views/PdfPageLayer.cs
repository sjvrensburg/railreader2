using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.Services;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class PdfPageLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public ColourEffectShaders? ColourEffects { get; set; }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new PageDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), Tab, ColourEffects));
    }

    private sealed class PageDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;

        public PageDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
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

            var tab = _tab;
            if (tab?.CachedImage is not { } image) return;

            // Apply colour effect via SaveLayer if active
            var effectPaint = _effects?.CreatePaint();
            if (effectPaint is not null)
                canvas.SaveLayer(effectPaint);

            // Draw page image scaled to page dimensions (points)
            // No camera transform here — that's handled by the parent's RenderTransform
            var destRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            canvas.DrawImage(image, destRect, sampling);

            if (effectPaint is not null)
            {
                canvas.Restore();
                effectPaint.Dispose();
            }
        }
    }
}
