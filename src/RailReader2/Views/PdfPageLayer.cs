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
    public bool MotionBlurEnabled { get; set; } = true;
    public double MotionBlurIntensity { get; set; } = 0.5;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Use tab page dimensions for the draw-op bounds so the compositor
        // does not clip the operation away when PageLayer.Bounds is still
        // zero (before the first layout pass after a tab is added).
        var tab = Tab;
        double w = tab?.PageWidth > 0 ? tab.PageWidth : Bounds.Width;
        double h = tab?.PageHeight > 0 ? tab.PageHeight : Bounds.Height;
        context.Custom(new PageDrawOperation(new Rect(0, 0, w, h), tab, ColourEffects, MotionBlurEnabled, MotionBlurIntensity));
    }

    private sealed class PageDrawOperation : ICustomDrawOperation
    {
        // Max sigma at full intensity; scaled by user's intensity setting
        private const float MaxBlurSigma = 0.35f;
        private const double MinSpeedThreshold = 0.1;

        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;
        private readonly bool _motionBlur;
        private readonly double _blurIntensity;

        public PageDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects, bool motionBlur, double blurIntensity)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
            _motionBlur = motionBlur;
            _blurIntensity = blurIntensity;
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

            // Motion blur: horizontal during rail scroll, uniform during zoom.
            // Cubic curve keeps blur barely visible at low/medium speeds,
            // only becoming noticeable near max speed.
            SKImageFilter? blurFilter = null;
            if (_motionBlur && _blurIntensity > 0)
            {
                float maxSigma = (float)(MaxBlurSigma * _blurIntensity);
                float sigmaX = 0, sigmaY = 0;

                if (tab.Rail.Active && tab.Rail.ScrollSpeed > MinSpeedThreshold)
                {
                    double s = tab.Rail.ScrollSpeed;
                    sigmaX = (float)(s * s * s * maxSigma);
                }

                if (tab.Camera.ZoomSpeed > MinSpeedThreshold)
                {
                    double z = tab.Camera.ZoomSpeed;
                    float zoomSigma = (float)(z * z * z * maxSigma);
                    sigmaX = Math.Max(sigmaX, zoomSigma);
                    sigmaY = Math.Max(sigmaY, zoomSigma);
                }

                if (sigmaX > 0 || sigmaY > 0)
                    blurFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY);
            }

            if (effectPaint is not null || blurFilter is not null)
            {
                var layerPaint = effectPaint ?? new SKPaint();
                if (blurFilter is not null)
                    layerPaint.ImageFilter = blurFilter;
                canvas.SaveLayer(layerPaint);
            }

            // Draw page image scaled to page dimensions (points)
            // No camera transform here — that's handled by the parent's RenderTransform
            var destRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            canvas.DrawImage(image, destRect, sampling);

            if (effectPaint is not null || blurFilter is not null)
            {
                canvas.Restore();
                effectPaint?.Dispose();
                blurFilter?.Dispose();
            }
        }
    }
}
