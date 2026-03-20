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

public class PdfPageLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public ColourEffectShaders? ColourEffects { get; set; }
    public ColourEffect ActiveEffect { get; set; }
    public float ActiveIntensity { get; set; } = 1.0f;
    public bool MotionBlurEnabled { get; set; } = true;
    public double MotionBlurIntensity { get; set; } = 0.5;
    public bool LineFocusBlurEnabled { get; set; }
    public double LineFocusBlurIntensity { get; set; } = 0.5;
    public double LineFocusPadding { get; set; } = 0.2;
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
        var opts = new RenderOptions(
            MotionBlurEnabled, MotionBlurIntensity,
            LineFocusBlurEnabled, LineFocusBlurIntensity, LineFocusPadding,
            BionicReadingEnabled, BionicFadeIntensity, BionicFadeRects,
            ActiveEffect, ActiveIntensity);
        context.Custom(new PageDrawOperation(new Rect(0, 0, w, h), tab, ColourEffects, opts));
    }

    public record struct RenderOptions(
        bool MotionBlur, double BlurIntensity,
        bool LineFocusBlur, double LineFocusIntensity, double LineFocusPadding,
        bool BionicEnabled, double BionicIntensity, List<SKRect>? BionicRects,
        ColourEffect ActiveEffect, float ActiveIntensity);

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
        [ThreadStatic] private static SKPaint? s_imagePaint;

        // Cached colour effect filter — reused when effect/intensity unchanged
        [ThreadStatic] private static SKColorFilter? s_cachedEffectFilter;
        [ThreadStatic] private static ColourEffect s_cachedEffectType;
        [ThreadStatic] private static float s_cachedEffectIntensity;

        // Cached bionic reading objects — reused when rects/intensity unchanged.
        // The rects list reference is stable (from GetOrComputeBionicOverlay cache),
        // so reference equality detects page/config changes without comparing contents.
        [ThreadStatic] private static SKColorFilter? s_cachedBionicFilter;
        [ThreadStatic] private static float s_cachedBionicIntensity;
        [ThreadStatic] private static SKPath? s_cachedBionicPath;
        [ThreadStatic] private static SKPaint? s_cachedBionicPaint;
        [ThreadStatic] private static List<SKRect>? s_cachedBionicRects;

        // Cached line focus dim paint — reused when line position unchanged
        private record struct DimCacheKey(
            float LineY, float LineH, float PageH,
            double Intensity, double Padding,
            ColourEffect Effect, float EffectIntensity);
        [ThreadStatic] private static SKPaint? s_cachedDimPaint;
        [ThreadStatic] private static SKShader? s_cachedDimGradient;
        [ThreadStatic] private static DimCacheKey s_cachedDimKey;

        // Cached sampling options — Mitchell for crisp text at rest, bilinear during animation
        private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);
        private static readonly SKSamplingOptions s_samplingFast = new(SKFilterMode.Linear);

        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;
        private readonly RenderOptions _opts;

        // Snapshot of dynamic tab state for Equals comparison.
        // When these match, the render output is identical and Avalonia can
        // skip re-executing the draw operation (only the parent transform changed).
        private readonly SKImage? _image;
        private readonly double _scrollSpeed, _zoomSpeed;
        private readonly float _lineY, _lineH;

        public PageDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects, RenderOptions opts)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
            _opts = opts;
            _image = tab?.CachedImage;
            _scrollSpeed = tab?.Rail.ScrollSpeed ?? 0;
            _zoomSpeed = tab?.Camera.ZoomSpeed ?? 0;
            if (tab?.Rail is { Active: true, NavigableCount: > 0 })
            {
                var line = tab.Rail.CurrentLineInfo;
                _lineY = line.Y;
                _lineH = line.Height;
            }
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is PageDrawOperation op
            && _bounds == op._bounds
            && _opts == op._opts
            && ReferenceEquals(_image, op._image)
            && _scrollSpeed == op._scrollSpeed
            && _zoomSpeed == op._zoomSpeed
            && _lineY == op._lineY
            && _lineH == op._lineH;
        public bool HitTest(Point p) => _bounds.Contains(p);

        /// <summary>
        /// Computes the dim overlay colour with the colour effect baked in.
        /// All SkSL shaders transform white via: result = mix(white, effect(white), intensity).
        /// For Invert/HighContrast/HighVisibility, effect(white) is black;
        /// for Amber, effect(white) is slightly warm. This avoids applying a colour
        /// filter to the gradient paint (which would corrupt transparent stops by
        /// transforming premultiplied-zero alpha pixels).
        /// </summary>
        private static SKColor ComputeDimColor(ColourEffect effect, float effectIntensity,
            double focusIntensity)
        {
            byte alpha = (byte)(255 * focusIntensity);
            return effect switch
            {
                ColourEffect.Invert or ColourEffect.HighContrast or ColourEffect.HighVisibility =>
                    new SKColor((byte)(255 * (1.0 - effectIntensity)),
                                (byte)(255 * (1.0 - effectIntensity)),
                                (byte)(255 * (1.0 - effectIntensity)), alpha),
                ColourEffect.Amber =>
                    new SKColor(255, 255, (byte)(255 * (1.0 - 0.15 * effectIntensity)), alpha),
                _ => new SKColor(255, 255, 255, alpha),
            };
        }

        /// <summary>
        /// Draws the page image with optional bionic fade integrated into the draw.
        /// Fixation regions draw at full contrast; fade regions draw with the bionic
        /// color filter. This must be called INSIDE any blur layers so blur applies
        /// uniformly on top.
        /// </summary>
        private void DrawPageImage(SKCanvas canvas, SKImage image, SKRect destRect,
            SKPath? bionicPath, SKPaint? bionicPaint, SKSamplingOptions sampling)
        {
            if (bionicPath is null || bionicPaint is null)
            {
                canvas.DrawImage(image, destRect, sampling);
                return;
            }

            // Fixation regions: full contrast (everything outside fade rects)
            canvas.Save();
            canvas.ClipPath(bionicPath, SKClipOperation.Difference);
            canvas.DrawImage(image, destRect, sampling);
            canvas.Restore();

            // Fade regions: bionic color filter applied
            canvas.Save();
            canvas.ClipPath(bionicPath);
            canvas.SaveLayer(bionicPaint);
            canvas.DrawImage(image, destRect, sampling);
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

            // Use bilinear during animation (scroll/zoom) for speed,
            // Mitchell cubic when stable for crisp text rendering.
            bool animating = _scrollSpeed > MinSpeedThreshold || _zoomSpeed > MinSpeedThreshold;
            var sampling = animating ? s_samplingFast : s_sampling;

            // Get or update cached colour effect filter
            SKColorFilter? effectFilter = null;
            if (_effects?.HasActiveEffect(_opts.ActiveEffect) == true)
            {
                if (s_cachedEffectFilter is null
                    || s_cachedEffectType != _opts.ActiveEffect
                    || s_cachedEffectIntensity != _opts.ActiveIntensity)
                {
                    s_cachedEffectFilter?.Dispose();
                    s_cachedEffectFilter = _effects.CreateColorFilter(_opts.ActiveEffect, _opts.ActiveIntensity);
                    s_cachedEffectType = _opts.ActiveEffect;
                    s_cachedEffectIntensity = _opts.ActiveIntensity;
                }
                effectFilter = s_cachedEffectFilter;
            }

            // Motion blur: horizontal during rail scroll, uniform during zoom.
            float sigmaX = 0, sigmaY = 0;
            if (_opts.MotionBlur && _opts.BlurIntensity > 0)
            {
                float maxSigma = (float)(MaxBlurSigma * _opts.BlurIntensity);

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
                    || Math.Abs(sigmaX - s_cachedSigmaX) > 0.05f
                    || Math.Abs(sigmaY - s_cachedSigmaY) > 0.05f)
                {
                    s_cachedBlurFilter?.Dispose();
                    s_cachedBlurFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY);
                    s_cachedSigmaX = sigmaX;
                    s_cachedSigmaY = sigmaY;
                }
                blurFilter = s_cachedBlurFilter;
            }

            var destRect = SKRect.Create(0, 0, (float)tab.PageWidth, (float)tab.PageHeight);

            // Get or update cached bionic reading objects
            SKPath? bionicPath = null;
            SKPaint? bionicPaint = null;
            if (_opts.BionicEnabled && _opts.BionicRects is { Count: > 0 } bionicRects && _effects is not null)
            {
                float bionicIntensity = (float)_opts.BionicIntensity;

                // Rebuild filter when intensity changes
                if (s_cachedBionicFilter is null || s_cachedBionicIntensity != bionicIntensity)
                {
                    s_cachedBionicFilter?.Dispose();
                    s_cachedBionicFilter = _effects.CreateBionicColorFilter(bionicIntensity);
                    s_cachedBionicIntensity = bionicIntensity;

                    // Filter changed — update paint
                    s_cachedBionicPaint?.Dispose();
                    s_cachedBionicPaint = new SKPaint { ColorFilter = s_cachedBionicFilter };
                }

                // Rebuild path when rect list instance changes (page change or config change)
                if (!ReferenceEquals(s_cachedBionicRects, bionicRects))
                {
                    s_cachedBionicPath?.Dispose();
                    var path = new SKPath();
                    foreach (var rect in bionicRects)
                        path.AddRect(rect);
                    s_cachedBionicPath = path;
                    s_cachedBionicRects = bionicRects;
                }

                bionicPath = s_cachedBionicPath;
                bionicPaint = s_cachedBionicPaint;
            }

            // SaveLayer creates a viewport-sized offscreen buffer — expensive on large
            // screens. Only use it when motion blur is active (image filter must operate
            // on the composite). For colour-filter-only mode, apply the filter directly
            // to each draw paint instead, avoiding the offscreen buffer entirely.
            // Line focus dim is always drawn OUTSIDE the SaveLayer (after Restore) to
            // prevent the blur filter from bleeding gradient edges into a visible halo.
            // The dim colour has the colour effect baked in via ComputeDimColor().
            bool needsBlurLayer = blurFilter is not null;
            bool hasBionic = bionicPath is not null;
            // Per-paint colour filter: apply directly when no blur and no bionic clip regions.
            bool perPaintFilter = effectFilter is not null && !needsBlurLayer && !hasBionic;
            // SaveLayer when blur is active, or when bionic clips need a uniform colour filter.
            bool useLayer = needsBlurLayer || (effectFilter is not null && hasBionic);
            if (useLayer)
            {
                s_layerPaint ??= new SKPaint();
                s_layerPaint.ColorFilter = effectFilter;
                s_layerPaint.ImageFilter = blurFilter;
                canvas.SaveLayer(s_layerPaint);
            }

            // Draw page image (with bionic if active)
            if (perPaintFilter)
            {
                // Apply colour filter directly to image paint — no SaveLayer needed
                s_imagePaint ??= new SKPaint();
                s_imagePaint.ColorFilter = effectFilter;
                var srcRect = SKRect.Create(image.Width, image.Height);
                canvas.DrawImage(image, srcRect, destRect, sampling, s_imagePaint);
            }
            else
            {
                DrawPageImage(canvas, image, destRect, bionicPath, bionicPaint, sampling);
            }

            if (useLayer)
            {
                canvas.Restore();
                s_layerPaint!.ColorFilter = null;
                s_layerPaint.ImageFilter = null;
            }

            // Line focus dim: draw a feathered overlay that dims everything
            // except the active line. Drawn OUTSIDE any blur SaveLayer to
            // avoid halo artifacts from the blur filter bleeding the gradient
            // edges. The dim colour always has the colour effect baked in
            // (via ComputeDimColor) since no colour-filter layer wraps it.
            if (_opts.LineFocusBlur && _opts.LineFocusIntensity > 0
                && tab.Rail is { Active: true, NavigableCount: > 0 } rail)
            {
                var line = rail.CurrentLineInfo;
                float h = (float)tab.PageHeight;

                // Always bake the colour effect into the dim colour directly.
                // Applying a colour filter to gradient paint corrupts transparent
                // pixels (premultiplied alpha violation), so we pre-compute the
                // filtered dim colour instead.
                var activeEffect = effectFilter is not null ? _opts.ActiveEffect : ColourEffect.None;
                float activeIntensity = effectFilter is not null ? _opts.ActiveIntensity : 0f;

                // Reuse cached dim paint when line position, settings, and effect are unchanged
                var dimKey = new DimCacheKey(line.Y, line.Height, h,
                    _opts.LineFocusIntensity, _opts.LineFocusPadding,
                    activeEffect, activeIntensity);
                if (s_cachedDimPaint is null || s_cachedDimKey != dimKey)
                {
                    s_cachedDimGradient?.Dispose();
                    s_cachedDimPaint?.Dispose();

                    float pad = line.Height * (float)_opts.LineFocusPadding;
                    float lineTop = line.Y - line.Height / 2f - pad;
                    float lineBottom = line.Y + line.Height / 2f + pad;
                    float feather = line.Height * 0.5f;

                    float featherTop = Math.Max(0, lineTop - feather) / h;
                    float featherBottom = Math.Min(h, lineBottom + feather) / h;
                    float normTop = Math.Clamp(lineTop / h, 0f, 1f);
                    float normBottom = Math.Clamp(lineBottom / h, 0f, 1f);

                    var dimColor = ComputeDimColor(activeEffect, activeIntensity,
                        _opts.LineFocusIntensity);
                    var clear = SKColors.Transparent;

                    s_cachedDimGradient = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0), new SKPoint(0, h),
                        [dimColor, dimColor, clear, clear, dimColor, dimColor],
                        [0f, featherTop, normTop, normBottom, featherBottom, 1f],
                        SKShaderTileMode.Clamp);
                    s_cachedDimPaint = new SKPaint { Shader = s_cachedDimGradient };
                    s_cachedDimKey = dimKey;
                }

                canvas.DrawRect(destRect, s_cachedDimPaint);
            }

            // effectFilter is cached (s_cachedEffectFilter) — do not dispose here
        }
    }
}
