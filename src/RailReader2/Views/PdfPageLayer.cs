using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable snapshot of all state needed to render one PDF page frame.
/// Built on the UI thread, sent to the composition thread via SendHandlerMessage.
/// </summary>
/// <summary>
/// Sent to the composition thread to dispose an SKImage that was replaced
/// by a DPI upgrade. Separate from render state to keep the state record pure.
/// </summary>
internal sealed record RetireImage(SKImage Image);

internal sealed record PdfPageRenderState(
    SKImage? Image,
    float PageW,
    float PageH,
    SKMatrix Camera,
    float ScrollSpeed,
    float ZoomSpeed,
    bool MotionBlur,
    float MotionBlurIntensity,
    bool LineFocusBlur,
    float LineFocusIntensity,
    float LinePadding,
    float LineY,
    float LineH,
    ColourEffect Effect,
    float EffectIntensity,
    ColourEffectShaders? Effects);

/// <summary>
/// Hosts a CompositionCustomVisual for PDF page rendering.
/// The visual applies the camera transform inside Skia, eliminating the
/// need for a MatrixTransform on the parent panel and the intermediate
/// compositing step that caused jitter on Windows/ANGLE.
/// </summary>
internal class PdfPageLayer : CompositionLayerControl<PdfPageVisualHandler>;


/// <summary>
/// Composition-thread handler for PDF page rendering.
/// All rendering runs on the compositor thread at the display's native refresh rate,
/// decoupled from the UI thread message pump.
/// </summary>
internal sealed class PdfPageVisualHandler : CompositionCustomVisualHandler
{
    private const float MaxBlurSigma = 0.35f;
    private const double MinSpeedThreshold = 0.1;
    private const float DimFeatherFraction = 0.08f;

    // Skip the mipmap chain only when the texture is clearly being *magnified* — the
    // on-screen footprint is at least this factor larger than the source image. That is the
    // high-magnification reading range (zoom above the DPI cap), where the mip chain is never
    // sampled and just wastes upload time + ~33% VRAM. Crucially, textures uploaded near 1:1
    // (or already minified) still get mips, so zooming such a texture back out doesn't
    // reintroduce texel-hop aliasing during the transient before Core re-rasters at lower DPI.
    // Defaulting to mips (the !magnified branch) is the quality-safe direction.
    private const float MipmapSkipMagnifyFactor = 1.25f;

    // ThreadStatic caches: one per composition thread (typically one per renderer)
    [ThreadStatic] private static SKImageFilter? s_cachedBlurFilter;
    [ThreadStatic] private static float s_cachedSigmaX, s_cachedSigmaY;
    [ThreadStatic] private static SKPaint? s_imagePaint;
    [ThreadStatic] private static SKColorFilter? s_cachedEffectFilter;
    [ThreadStatic] private static ColourEffect s_cachedEffectType;
    [ThreadStatic] private static float s_cachedEffectIntensity;

    private record struct DimCacheKey(
        float LineY, float LineH, float PageH,
        float Intensity, float Padding,
        ColourEffect Effect, float EffectIntensity);
    [ThreadStatic] private static SKPaint? s_cachedDimPaint;
    [ThreadStatic] private static SKShader? s_cachedDimGradient;
    [ThreadStatic] private static DimCacheKey s_cachedDimKey;

    // Mitchell cubic for crisp text at rest; trilinear for smooth downsampling
    // at low zoom during animation (mip chain eliminates texel-hop aliasing).
    private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);
    private static readonly SKSamplingOptions s_samplingFast =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private PdfPageRenderState? _state;

    // GPU texture cache with mipmaps for alias-free downsampling.
    // The source SKImage from TabViewModel is a raster image without mipmaps;
    // ToTextureImage uploads it to the GPU with a full mip chain.
    private SKImage? _gpuTexture;
    private SKImage? _gpuTextureSource; // tracks which raster image was uploaded

    public override void OnMessage(object message)
    {
        if (message is RetireImage retire)
        {
            // Dispose the old SKImage on the composition thread where we
            // know OnRender is not concurrently accessing it.
            retire.Image.Dispose();
            return;
        }

        if (message is PdfPageRenderState state)
        {
            // Invalidate GPU texture cache when source image changes
            if (!ReferenceEquals(state.Image, _gpuTextureSource))
            {
                _gpuTexture?.Dispose();
                _gpuTexture = null;
                _gpuTextureSource = null;
            }

            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state?.Image is not { } image) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        // Upload raster image as a GPU texture. A mip chain fixes texel-hop aliasing while the
        // texture is minified, but it costs upload time and ~33% VRAM and is never sampled while
        // the texture is magnified (upscaled). Skip it only when this image is clearly being
        // magnified; build it for near-1:1 and minified uploads so a later zoom-out doesn't
        // shimmer before Core re-rasters. canvas.TotalMatrix here (before the camera concat
        // below) carries the compositor's DPI scale; × the camera zoom gives
        // device-pixels-per-page-unit, which against image.Width tells us magnification.
        var grContext = lease.GrContext;
        if (grContext is not null && !ReferenceEquals(image, _gpuTextureSource))
        {
            float deviceWidth = state.PageW * canvas.TotalMatrix.ScaleX * state.Camera.ScaleX;
            bool magnified = deviceWidth > image.Width * MipmapSkipMagnifyFactor;
            _gpuTexture?.Dispose();
            _gpuTexture = image.ToTextureImage(grContext, mipmapped: !magnified);
            _gpuTextureSource = image;
        }
        var drawImage = _gpuTexture ?? image;

        // Apply camera transform on top of the compositor's existing matrix
        // (which already contains DPI scaling). This is the key architectural
        // difference from the old design: camera + draw are atomic in one
        // compositor pass, so there is no stale-draw/new-transform frame mismatch.
        canvas.Save();
        canvas.Concat(state.Camera);

        bool animating = state.ScrollSpeed > MinSpeedThreshold || state.ZoomSpeed > MinSpeedThreshold;
        var sampling = animating ? s_samplingFast : s_sampling;

        // Colour effect filter
        SKColorFilter? effectFilter = null;
        if (state.Effects?.HasActiveEffect(state.Effect) == true)
        {
            if (s_cachedEffectFilter is null
                || s_cachedEffectType != state.Effect
                || Math.Abs(s_cachedEffectIntensity - state.EffectIntensity) > 0.001f)
            {
                s_cachedEffectFilter?.Dispose();
                s_cachedEffectFilter = state.Effects.CreateColorFilter(state.Effect, state.EffectIntensity);
                s_cachedEffectType = state.Effect;
                s_cachedEffectIntensity = state.EffectIntensity;
            }
            effectFilter = s_cachedEffectFilter;
        }

        // Motion blur: horizontal during rail scroll, uniform during zoom.
        // Camera.ScaleX == zoom factor. Dividing sigma by zoom keeps screen-pixel
        // blur constant regardless of zoom level (sigma is in page/canvas units).
        float sigmaX = 0, sigmaY = 0;
        if (state.MotionBlur && state.MotionBlurIntensity > 0)
        {
            float maxSigma = state.MotionBlurIntensity * MaxBlurSigma;
            float zoom = Math.Max(state.Camera.ScaleX, 0.01f);

            if (state.ScrollSpeed > MinSpeedThreshold)
            {
                double s = state.ScrollSpeed;
                sigmaX = (float)(s * s * s * maxSigma) / zoom;
            }
            if (state.ZoomSpeed > MinSpeedThreshold)
            {
                double z = state.ZoomSpeed;
                float zSigma = (float)(z * z * z * maxSigma) / zoom;
                sigmaX = Math.Max(sigmaX, zSigma);
                sigmaY = Math.Max(sigmaY, zSigma);
            }
        }

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

        var destRect = SKRect.Create(0, 0, state.PageW, state.PageH);

        // Apply the colour effect and/or motion blur directly on the DrawImage paint rather
        // than through canvas.SaveLayer(). A SaveLayer allocates a viewport-sized offscreen
        // buffer every animation frame — the dominant per-frame GPU cost on large/ultrawide
        // displays — whereas setting the filters on the image paint lets Skia filter just the
        // image primitive. Visually identical: one image draw, then the unblurred dim gradient
        // below (drawn after, with no filters, exactly as the post-Restore draw did before).
        if (effectFilter is not null || blurFilter is not null)
        {
            s_imagePaint ??= new SKPaint();
            s_imagePaint.ColorFilter = effectFilter;
            s_imagePaint.ImageFilter = blurFilter;
            var srcRect = SKRect.Create(drawImage.Width, drawImage.Height);
            canvas.DrawImage(drawImage, srcRect, destRect, sampling, s_imagePaint);
            // Don't let the cached paint retain refs to filters that may be disposed
            // (effect/intensity or blur sigma change) before the next frame reassigns them.
            s_imagePaint.ColorFilter = null;
            s_imagePaint.ImageFilter = null;
        }
        else
        {
            canvas.DrawImage(drawImage, destRect, sampling);
        }

        // Line focus dim: feathered gradient outside the active line.
        // Drawn after the image (and with its own filter-free paint) so it isn't blurred itself.
        // The colour effect is baked into the dim colour to avoid applying a
        // colour filter to the gradient paint (premultiplied alpha corruption).
        if (state.LineFocusBlur && state.LineFocusIntensity > 0 && state.LineH > 0)
        {
            float h = state.PageH;
            var activeEffect = effectFilter is not null ? state.Effect : ColourEffect.None;
            float activeIntensity = effectFilter is not null ? state.EffectIntensity : 0f;

            var dimKey = new DimCacheKey(state.LineY, state.LineH, h,
                state.LineFocusIntensity, state.LinePadding, activeEffect, activeIntensity);
            if (s_cachedDimPaint is null || s_cachedDimKey != dimKey)
            {
                s_cachedDimGradient?.Dispose();
                s_cachedDimPaint?.Dispose();

                float pad = state.LineH * state.LinePadding;
                float lineTop = state.LineY - state.LineH / 2f - pad;
                float lineBottom = state.LineY + state.LineH / 2f + pad;
                float feather = state.LineH * DimFeatherFraction;

                float featherTop = Math.Max(0, lineTop - feather) / h;
                float featherBottom = Math.Min(h, lineBottom + feather) / h;
                float normTop = Math.Clamp(lineTop / h, 0f, 1f);
                float normBottom = Math.Clamp(lineBottom / h, 0f, 1f);

                var dimColor = ComputeDimColor(activeEffect, activeIntensity, state.LineFocusIntensity);
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

        canvas.Restore(); // undo camera concat
    }

    private static SKColor ComputeDimColor(ColourEffect effect, float effectIntensity, float focusIntensity)
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
}
