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

        // Cached line focus dim paint — reused when line position unchanged
        [ThreadStatic] private static SKPaint? s_cachedDimPaint;
        [ThreadStatic] private static SKShader? s_cachedDimGradient;
        [ThreadStatic] private static float s_cachedDimLineY, s_cachedDimLineH, s_cachedDimPageH;
        [ThreadStatic] private static double s_cachedDimIntensity, s_cachedDimPadding;

        // Cached sampling options (struct, but avoids repeated construction)
        private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);

        private readonly Rect _bounds;
        private readonly TabViewModel? _tab;
        private readonly ColourEffectShaders? _effects;
        private readonly RenderOptions _opts;

        public PageDrawOperation(Rect bounds, TabViewModel? tab, ColourEffectShaders? effects, RenderOptions opts)
        {
            _bounds = bounds;
            _tab = tab;
            _effects = effects;
            _opts = opts;
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

            // Build bionic clip path + paint once for use in all draw calls
            using var bionicFilter = (_opts.BionicEnabled && _opts.BionicRects is { Count: > 0 } && _effects is not null)
                ? _effects.CreateBionicColorFilter((float)_opts.BionicIntensity) : null;
            SKPath? bp = null;
            if (bionicFilter is not null && _opts.BionicRects is { } fadeRects)
            {
                bp = new SKPath();
                foreach (var rect in fadeRects)
                    bp.AddRect(rect);
            }
            using var bionicPath = bp;
            using var bionicPaint = bionicFilter is not null ? new SKPaint { ColorFilter = bionicFilter } : null;

            // SaveLayer creates a viewport-sized offscreen buffer — expensive on large
            // screens. Only use it when motion blur is active (image filter must operate
            // on the composite). For colour-filter-only mode, apply the filter directly
            // to each draw paint instead, avoiding the offscreen buffer entirely.
            // Line focus dim: applying the colour filter per-paint gives
            // blend(filter(page), filter(dim)) instead of filter(blend(page, dim)).
            // These are equivalent for Invert (proven algebraically) and visually
            // indistinguishable for other effects because all SkSL shaders preserve
            // alpha (return half4(result, color.a)), so the gradient transparency
            // is maintained and the dim colour is correctly filtered.
            bool needsBlurLayer = blurFilter is not null;
            bool hasBionic = bionicPath is not null;
            // Per-paint colour filter: when no blur and no bionic clip regions.
            bool perPaintFilter = effectFilter is not null && !needsBlurLayer && !hasBionic;

            // Use SaveLayer only when blur is active or bionic needs uniform filter.
            bool useLayer = needsBlurLayer || (effectFilter is not null && !perPaintFilter);
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
                canvas.DrawImage(image, srcRect, destRect, s_sampling, s_imagePaint);
            }
            else
            {
                DrawPageImage(canvas, image, destRect, bionicPath, bionicPaint);
            }

            // Line focus dim: draw a feathered white overlay that dims
            // everything except the active line, with smooth transitions.
            // Uses a vertical linear gradient with 6 stops to create a
            // transparent window at the active line that feathers to full
            // dim on both sides. Drawn inside the colour effect layer so
            // the white adapts to colour effects (e.g. becomes black for Invert).
            if (_opts.LineFocusBlur && _opts.LineFocusIntensity > 0
                && tab.Rail is { Active: true, NavigableCount: > 0 } rail)
            {
                var line = rail.CurrentLineInfo;
                float h = (float)tab.PageHeight;

                // Reuse cached dim paint when line position and settings are unchanged
                if (s_cachedDimPaint is null
                    || s_cachedDimLineY != line.Y
                    || s_cachedDimLineH != line.Height
                    || s_cachedDimPageH != h
                    || s_cachedDimIntensity != _opts.LineFocusIntensity
                    || s_cachedDimPadding != _opts.LineFocusPadding)
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

                    var dimColor = new SKColor(255, 255, 255, (byte)(255 * _opts.LineFocusIntensity));
                    var clear = SKColors.Transparent;

                    s_cachedDimGradient = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0), new SKPoint(0, h),
                        [dimColor, dimColor, clear, clear, dimColor, dimColor],
                        [0f, featherTop, normTop, normBottom, featherBottom, 1f],
                        SKShaderTileMode.Clamp);
                    s_cachedDimPaint = new SKPaint { Shader = s_cachedDimGradient };

                    s_cachedDimLineY = line.Y;
                    s_cachedDimLineH = line.Height;
                    s_cachedDimPageH = h;
                    s_cachedDimIntensity = _opts.LineFocusIntensity;
                    s_cachedDimPadding = _opts.LineFocusPadding;
                }

                // When using per-paint filter (no SaveLayer), apply colour filter
                // to the dim paint so it matches the effect (e.g. white→black for Invert).
                if (perPaintFilter)
                    s_cachedDimPaint!.ColorFilter = effectFilter;

                canvas.DrawRect(destRect, s_cachedDimPaint);

                if (perPaintFilter)
                    s_cachedDimPaint!.ColorFilter = null;
            }

            if (useLayer)
            {
                canvas.Restore();
                s_layerPaint!.ColorFilter = null;
                s_layerPaint.ImageFilter = null;
            }

            // effectFilter is cached (s_cachedEffectFilter) — do not dispose here
        }
    }
}
