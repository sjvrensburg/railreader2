using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader.Core.Services;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class PdfPageLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public ColourEffectShaders? ColourEffects { get; set; }
    public bool MotionBlurEnabled { get; set; } = true;
    public double MotionBlurIntensity { get; set; } = 0.5;
    public bool LineFocusBlurEnabled { get; set; }
    public double LineFocusBlurIntensity { get; set; } = 0.5;
    public bool BionicReadingEnabled { get; set; }
    public double BionicFadeIntensity { get; set; } = 0.6;
    public List<SKRect>? BionicFadeRects { get; set; }

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
        context.Custom(new PageDrawOperation(new Rect(0, 0, w, h), tab, ColourEffects,
            MotionBlurEnabled, MotionBlurIntensity, LineFocusBlurEnabled, LineFocusBlurIntensity,
            BionicReadingEnabled, BionicFadeIntensity, BionicFadeRects));
    }

    private sealed class PageDrawOperation : ICustomDrawOperation
    {
        // Max sigma at full intensity; scaled by user's intensity setting
        private const float MaxBlurSigma = 0.35f;
        private const double MinSpeedThreshold = 0.1;

        // Cached blur filter — reused when sigma values haven't changed
        [ThreadStatic] private static SKImageFilter? s_cachedBlurFilter;
        [ThreadStatic] private static float s_cachedSigmaX, s_cachedSigmaY;

        // Cached paint objects to avoid per-frame allocation
        [ThreadStatic] private static SKPaint? s_layerPaint;

        // Cached sampling options (struct, but avoids repeated construction)
        private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);

        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;
        private readonly bool _motionBlur;
        private readonly double _blurIntensity;
        private readonly bool _lineFocusBlur;
        private readonly double _lineFocusIntensity;
        private readonly bool _bionicEnabled;
        private readonly double _bionicIntensity;
        private readonly List<SKRect>? _bionicRects;

        public PageDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects,
            bool motionBlur, double blurIntensity, bool lineFocusBlur, double lineFocusIntensity,
            bool bionicEnabled, double bionicIntensity, List<SKRect>? bionicRects)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
            _motionBlur = motionBlur;
            _blurIntensity = blurIntensity;
            _lineFocusBlur = lineFocusBlur;
            _lineFocusIntensity = lineFocusIntensity;
            _bionicEnabled = bionicEnabled;
            _bionicIntensity = bionicIntensity;
            _bionicRects = bionicRects;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => _bounds.Contains(p);

        /// <summary>
        /// Draws the page image with optional bionic fade integrated into the draw.
        /// Fixation regions draw at full contrast; fade regions draw with the bionic
        /// color filter. This must be called INSIDE any blur layers so blur applies
        /// uniformly on top.
        /// </summary>
        private void DrawPageImage(SKCanvas canvas, SKImage image, SKRect destRect,
            SKPath? bionicPath, SKPaint? bionicPaint)
        {
            if (bionicPath is null || bionicPaint is null)
            {
                canvas.DrawImage(image, destRect, s_sampling);
                return;
            }

            // Fixation regions: full contrast (everything outside fade rects)
            canvas.Save();
            canvas.ClipPath(bionicPath, SKClipOperation.Difference);
            canvas.DrawImage(image, destRect, s_sampling);
            canvas.Restore();

            // Fade regions: bionic color filter applied
            canvas.Save();
            canvas.ClipPath(bionicPath);
            canvas.SaveLayer(bionicPaint);
            canvas.DrawImage(image, destRect, s_sampling);
            canvas.Restore(); // layer
            canvas.Restore(); // clip
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var tab = _tab;
            if (tab?.CachedImage is not { } image) return;

            var effectPaint = _effects?.CreatePaint();

            // Motion blur: horizontal during rail scroll, uniform during zoom.
            float sigmaX = 0, sigmaY = 0;
            if (_motionBlur && _blurIntensity > 0)
            {
                float maxSigma = (float)(MaxBlurSigma * _blurIntensity);

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
            }

            // Get or update cached motion blur filter
            SKImageFilter? blurFilter = null;
            if (sigmaX > 0 || sigmaY > 0)
            {
                if (s_cachedBlurFilter is null
                    || Math.Abs(sigmaX - s_cachedSigmaX) > 0.001f
                    || Math.Abs(sigmaY - s_cachedSigmaY) > 0.001f)
                {
                    s_cachedBlurFilter?.Dispose();
                    s_cachedBlurFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY);
                    s_cachedSigmaX = sigmaX;
                    s_cachedSigmaY = sigmaY;
                }
                blurFilter = s_cachedBlurFilter;
            }

            // Outer layer: colour effect + motion blur. Everything drawn inside
            // this layer gets the colour filter applied uniformly (once).
            bool needsLayer = effectPaint is not null || blurFilter is not null;
            if (needsLayer)
            {
                s_layerPaint ??= new SKPaint();
                s_layerPaint.ColorFilter = effectPaint?.ColorFilter;
                s_layerPaint.ImageFilter = blurFilter;
                canvas.SaveLayer(s_layerPaint);
            }

            var destRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);

            // Build bionic clip path + paint once for use in all draw calls
            SKPath? bionicPath = null;
            SKPaint? bionicPaint = null;
            SKColorFilter? bionicFilter = null;
            if (_bionicEnabled && _bionicRects is { Count: > 0 } fadeRects && _effects is not null)
            {
                bionicFilter = _effects.CreateBionicColorFilter((float)_bionicIntensity);
                if (bionicFilter is not null)
                {
                    bionicPath = new SKPath();
                    foreach (var rect in fadeRects)
                        bionicPath.AddRect(rect);
                    bionicPaint = new SKPaint { ColorFilter = bionicFilter };
                }
            }

            // Line focus blur: draw the page in two passes inside the colour
            // effect layer so blur doesn't double-apply the tint.
            // Pass 1: blurred page everywhere except the active line.
            // Pass 2: sharp active line on top.
            // Bionic fade is integrated into each pass so it composes correctly
            // with the blur (faded text gets blurred on non-active lines).
            bool didLineFocusBlur = false;
            if (_lineFocusBlur && _lineFocusIntensity > 0
                && tab.Rail is { Active: true, NavigableCount: > 0 } rail)
            {
                var line = rail.CurrentLineInfo;
                // Pad the sharp region by 25% of line height on each side so
                // descenders (g, q, y) and ascenders aren't clipped by the blur.
                float pad = line.Height * 0.25f;
                // Snap clip rect edges to integer page-space pixels to prevent
                // shimmer at the blur boundary during sub-pixel camera panning.
                float lineTop = MathF.Floor(line.Y - line.Height / 2f - pad);
                float lineBottom = MathF.Ceiling(line.Y + line.Height / 2f + pad);
                // Use full page width so blur extends beyond the block
                var lineRect = new SKRect(0, lineTop, (float)tab.PageWidth, lineBottom);

                float sigma = (float)(4.0 * _lineFocusIntensity);
                if (sigma >= 0.5f)
                {
                    didLineFocusBlur = true;

                    // Pass 1: Draw entire page blurred, clipping out the active line
                    canvas.Save();
                    canvas.ClipRect(lineRect, SKClipOperation.Difference);
                    using var focusBlur = SKImageFilter.CreateBlur(sigma, sigma);
                    using var focusPaint = new SKPaint { ImageFilter = focusBlur };
                    canvas.SaveLayer(focusPaint);
                    DrawPageImage(canvas, image, destRect, bionicPath, bionicPaint);
                    canvas.Restore(); // layer
                    canvas.Restore(); // clip

                    // Pass 2: Draw just the active line sharp
                    canvas.Save();
                    canvas.ClipRect(lineRect);
                    DrawPageImage(canvas, image, destRect, bionicPath, bionicPaint);
                    canvas.Restore(); // clip
                }
            }

            // If line focus blur didn't handle the page draw, do it now
            if (!didLineFocusBlur)
                DrawPageImage(canvas, image, destRect, bionicPath, bionicPaint);

            // Dispose bionic resources
            bionicPath?.Dispose();
            bionicPaint?.Dispose();
            bionicFilter?.Dispose();

            if (needsLayer)
            {
                canvas.Restore();
                s_layerPaint!.ColorFilter = null;
                s_layerPaint.ImageFilter = null;
            }

            effectPaint?.Dispose();
        }
    }
}
