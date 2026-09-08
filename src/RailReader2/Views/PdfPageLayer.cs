using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core;
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

/// <summary>
/// One visible page's own draw: its rasterised image (null while its render is still in flight —
/// draw the gap colour, which is simply the ViewportPanel's own background showing through) and its
/// page-anchored camera matrix (see <c>Viewport.PageOffset</c>/<c>VisiblePages</c>). In single-page
/// mode there is always exactly one <see cref="PageDraw"/>, with <see cref="IsAnchor"/> true — the
/// draw loop below then behaves identically to the old single-image path.
/// </summary>
internal readonly record struct PageDraw(
    int Page, bool IsAnchor, SKImage? Image, float PageW, float PageH, SKMatrix Camera);

internal sealed record PdfPageRenderState(
    IReadOnlyList<PageDraw> Pages,
    float Zoom,
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
    private const double MinSpeedThreshold = 0.1;
    private const float DimFeatherFraction = 0.08f;

    // Motion-blur sigma at full speed, in DEVICE pixels at intensity 1.0. The old constant (0.35, in
    // page units, divided by zoom) worked out to <= intensity * 0.35 px on screen — invisible, but still
    // a full Gaussian pass every animating frame (#223). 3.0f (the first tuning) turned out to still be
    // imperceptible at the shipped default intensity (0.33 -> <= ~1px, only right at the end of a
    // sustained ~1.5s scroll/zoom hold) — raised to 6.0f so a full-speed hold reaches ~2px, a real,
    // visible smear rather than a rounding error.
    private const float MotionBlurMaxDeviceSigma = 6.0f;

    // Continuous-scroll line-focus blur (non-anchor visible pages): sigma-per-intensity-unit, in
    // page-point-times-zoom canvas units (the canvas here is already scaled by the camera concat) —
    // mirrors RailReaderCore's ScreenshotCompositor.RenderPageContinuous reference implementation so
    // the live viewport and an offline continuous screenshot agree on the same math.
    private const float NeighborFocusSigmaPerIntensity = 4.0f;
    private const float MinBlurSigma = 0.5f;

    // Skip the mipmap chain only when the texture is clearly being *magnified* — the
    // on-screen footprint is at least this factor larger than the source image. That is the
    // high-magnification reading range (zoom above the DPI cap), where the mip chain is never
    // sampled and just wastes upload time + ~33% VRAM. Crucially, textures uploaded near 1:1
    // (or already minified) still get mips, so zooming such a texture back out doesn't
    // reintroduce texel-hop aliasing during the transient before Core re-rasters at lower DPI.
    // Defaulting to mips (the !magnified branch) is the quality-safe direction.
    private const float MipmapSkipMagnifyFactor = 1.25f;

    // Diagnostic-only: times GetOrUploadTexture's ToTextureImage call and logs it when it exceeds
    // GpuUploadTimingLogThresholdMs. Off by default — gated behind RR_GPU_UPLOAD_TIMING=1 so we never
    // pay a synchronous, flushing ConsoleLogger write on the composition thread in normal use (#222
    // Phase 1). Parsed once; the field itself is the only per-frame cost when disabled.
    private static readonly bool s_gpuUploadTimingEnabled =
        Environment.GetEnvironmentVariable("RR_GPU_UPLOAD_TIMING") == "1";
    private const double GpuUploadTimingLogThresholdMs = 3.0;

    // ThreadStatic caches: one per composition thread (typically one per renderer)
    [ThreadStatic] private static SKPaint? s_imagePaint;
    [ThreadStatic] private static SKColorFilter? s_cachedEffectFilter;
    [ThreadStatic] private static ColourEffect s_cachedEffectType;
    [ThreadStatic] private static float s_cachedEffectIntensity;

    // Motion blur (anchor page — identical to single-page mode's only blur before continuous scroll).
    [ThreadStatic] private static SKImageFilter? s_cachedBlurFilter;
    [ThreadStatic] private static float s_cachedSigmaX, s_cachedSigmaY;
    // Combined motion + line-focus blur, shared by every NON-anchor visible page (they all share the
    // same zoom/scroll/focus sigma, so one cached filter serves the whole window).
    [ThreadStatic] private static SKImageFilter? s_cachedNeighborBlurFilter;
    [ThreadStatic] private static float s_cachedNeighborSigmaX, s_cachedNeighborSigmaY;

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

    // GPU texture cache with mipmaps for alias-free downsampling, one entry per currently-visible
    // page (single-page mode: at most one entry — the anchor). The source SKImage from
    // ViewportImages is a raster image without mipmaps; ToTextureImage uploads it to the GPU with a
    // full mip chain. Pruned in OnMessage whenever a page drops out of the new state's Pages list;
    // an entry whose source image changed (DPI upgrade) is re-uploaded lazily in OnRender.
    private readonly Dictionary<int, (SKImage Texture, SKImage Source)> _gpuTextures = new();

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
            if (_gpuTextures.Count > 0)
            {
                var wanted = new HashSet<int>();
                foreach (var p in state.Pages) wanted.Add(p.Page);
                List<int>? stale = null;
                foreach (var key in _gpuTextures.Keys)
                    if (!wanted.Contains(key)) (stale ??= new List<int>()).Add(key);
                if (stale is not null)
                    foreach (var key in stale)
                    {
                        _gpuTextures[key].Texture.Dispose();
                        _gpuTextures.Remove(key);
                    }
            }

            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null || state.Pages.Count == 0) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        var grContext = lease.GrContext;

        bool animating = state.ScrollSpeed > MinSpeedThreshold || state.ZoomSpeed > MinSpeedThreshold;
        var sampling = animating ? s_samplingFast : s_sampling;

        // Colour effect filter — shared across every visible page.
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

        // Motion blur: horizontal during rail scroll, uniform during zoom. Camera zoom is shared by
        // every visible page (they're all views of the same viewport), so this sigma is document-wide.
        float motionSigmaX = 0, motionSigmaY = 0;
        if (state.MotionBlur && state.MotionBlurIntensity > 0)
        {
            float zoom = Math.Max(state.Zoom, 0.01f);
            // canvas.TotalMatrix is read BEFORE the per-page camera concat, so ScaleX/ScaleY are the
            // compositor's DPI scale; the camera adds `zoom` on top. Skia maps the filter sigma through
            // the full CTM, so divide the wanted device sigma by both to get the local-space value to
            // hand the filter. Computed per-axis (not a single shared scale) in case the compositor CTM
            // is ever anisotropic (e.g. non-uniform display scaling).
            float ctmScaleX = Math.Max(canvas.TotalMatrix.ScaleX * zoom, 0.0001f);
            float ctmScaleY = Math.Max(canvas.TotalMatrix.ScaleY * zoom, 0.0001f);
            float maxDevice = state.MotionBlurIntensity * MotionBlurMaxDeviceSigma;

            float deviceX = 0, deviceY = 0;
            if (state.ScrollSpeed > MinSpeedThreshold)
            {
                double s = state.ScrollSpeed;
                deviceX = (float)(s * s * s * maxDevice);
            }
            if (state.ZoomSpeed > MinSpeedThreshold)
            {
                double z = state.ZoomSpeed;
                float zDevice = (float)(z * z * z * maxDevice);
                deviceX = Math.Max(deviceX, zDevice);
                deviceY = Math.Max(deviceY, zDevice);
            }

            // Below half a device pixel the blur is imperceptible but still costs a full image-filter
            // pass over the visible region — skip it entirely (#223).
            if (deviceX >= MinBlurSigma || deviceY >= MinBlurSigma)
            {
                motionSigmaX = deviceX / ctmScaleX;
                motionSigmaY = deviceY / ctmScaleY;
            }
        }
        var motionBlurFilter = GetCachedBlurFilter(motionSigmaX, motionSigmaY);

        // Non-anchor visible pages have no seated line to keep sharp, so line-focus mode blurs them
        // in full instead of dimming (the anchor's own treatment, applied below via the feathered
        // gradient). Composed with motion blur (independent Gaussians combine as sqrt(sum of squares))
        // rather than SaveLayer-stacking two passes — SaveLayer allocates a viewport-sized offscreen
        // buffer every frame (see railreader2's rendering-performance notes), so every filter here is
        // applied directly on the DrawImage paint instead, same as the motion-blur-only path always
        // has been.
        SKImageFilter? neighborBlurFilter = motionBlurFilter;
        if (state.LineFocusBlur && state.LineFocusIntensity > 0 && state.LineH > 0)
        {
            float focusSigma = NeighborFocusSigmaPerIntensity * state.LineFocusIntensity;
            if (focusSigma >= MinBlurSigma)
            {
                float nSigmaX = MathF.Sqrt(motionSigmaX * motionSigmaX + focusSigma * focusSigma);
                float nSigmaY = MathF.Sqrt(motionSigmaY * motionSigmaY + focusSigma * focusSigma);
                neighborBlurFilter = GetCachedNeighborBlurFilter(nSigmaX, nSigmaY);
            }
        }

        // Per-frame upload budget (#222): the draw always maps the whole source image to the whole
        // page rect, so a texture from a different DPI tier still draws to the geometrically correct
        // place — just softer for a frame or two. That makes it safe to defer a non-anchor page's
        // upload and keep drawing its stale resident texture (or skip it if it has none yet) rather
        // than stalling this frame on every visible page's upload at once (continuous scroll).
        int nonAnchorUploadBudget = 1;
        bool deferredUpload = false;

        foreach (var page in state.Pages)
        {
            if (page.Image is null) continue; // render in flight — the panel's own background is the gap colour

            var image = page.Image;
            float deviceWidth = page.PageW * canvas.TotalMatrix.ScaleX * state.Zoom;

            SKImage drawImage;
            if (grContext is null)
            {
                // No GPU context available (e.g. software fallback) — draw the raster source directly,
                // same as always; texture caching/budgeting doesn't apply. Deliberately does NOT fall
                // back to a previously-uploaded GPU texture for this page even if one is still resident
                // in _gpuTextures: a texture created under a GPU context can be invalidated by the very
                // loss of that context, so drawing the raw raster source here is the safe choice, not
                // just the simple one.
                drawImage = image;
            }
            else
            {
                bool haveEntry = _gpuTextures.TryGetValue(page.Page, out var cached);
                bool haveCurrent = haveEntry && ReferenceEquals(cached.Source, image);
                if (haveCurrent)
                {
                    drawImage = cached.Texture;
                }
                else if (page.IsAnchor || nonAnchorUploadBudget > 0)
                {
                    // The anchor always gets its upload — it's what the user is reading. A budgeted
                    // non-anchor upload consumes the frame's single slot.
                    if (!page.IsAnchor) nonAnchorUploadBudget--;
                    drawImage = UploadTexture(page.Page, image, grContext, deviceWidth);
                }
                else if (haveEntry)
                {
                    // Out of budget this frame: draw the stale resident texture (`cached`, still
                    // geometrically correct — see the OnRender-level comment above) and try again next
                    // frame.
                    drawImage = cached.Texture;
                    deferredUpload = true;
                }
                else
                {
                    // No resident texture at all yet — nothing to draw this frame.
                    deferredUpload = true;
                    continue;
                }
            }

            canvas.Save();
            canvas.Concat(page.Camera);

            var destRect = SKRect.Create(0, 0, page.PageW, page.PageH);
            var blurFilter = page.IsAnchor ? motionBlurFilter : neighborBlurFilter;

            // Apply the colour effect and/or blur directly on the DrawImage paint rather than through
            // canvas.SaveLayer() — see the comment above; one image draw, then the anchor's unblurred
            // dim gradient (below), exactly as the single-page path always did.
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

            // Line focus dim: feathered gradient outside the active line, anchor page only (the seated
            // rail line only ever exists there — every other visible page was already blurred in full
            // above). Drawn after the image (and with its own filter-free paint) so it isn't blurred
            // itself. The colour effect is baked into the dim colour to avoid applying a colour filter
            // to the gradient paint (premultiplied alpha corruption).
            if (page.IsAnchor && state.LineFocusBlur && state.LineFocusIntensity > 0 && state.LineH > 0)
            {
                float h = page.PageH;
                var activeEffect = effectFilter is not null ? state.Effect : ColourEffect.None;
                var activeIntensity = effectFilter is not null ? state.EffectIntensity : 0f;

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

        // A page was left with a stale or missing texture this frame — schedule another frame so the
        // deferred upload(s) drain at one per frame. Termination is guaranteed because every such
        // frame uploads at least one texture (the budgeted slot, or the anchor's own).
        if (deferredUpload) Invalidate();
    }

    private SKImage UploadTexture(int page, SKImage source, GRContext grContext, float deviceWidth)
    {
        // Upload raster image as a GPU texture. A mip chain fixes texel-hop aliasing while the
        // texture is minified, but it costs upload time and ~33% VRAM and is never sampled while the
        // texture is magnified (upscaled). Skip it only when this image is clearly being magnified;
        // build it for near-1:1 and minified uploads so a later zoom-out doesn't shimmer before Core
        // re-rasters. deviceWidth (device-pixels-per-page-width) against source.Width tells us
        // magnification.
        bool magnified = deviceWidth > source.Width * MipmapSkipMagnifyFactor;
        _gpuTextures.TryGetValue(page, out var old);

        // Upload before disposing the old texture: ToTextureImage can throw (e.g. GPU OOM — the exact
        // pressure this budgeting exists to reduce), and disposing `old` first would leave a
        // now-invalid SKImage keyed in _gpuTextures, double-disposed on the next retry. Keeping `old`
        // alive until the new texture is resident also means a throw here leaves the page's existing
        // (still-valid, still-drawable) texture in place rather than the page going dark.
        // The upload call itself stays a single call site — only the Stopwatch/logging around it are
        // conditional on the diagnostic flag, so a future change to the call (parameters, try/catch)
        // can't land in only one of two copies.
        SKImage texture;
        Stopwatch? sw = s_gpuUploadTimingEnabled ? Stopwatch.StartNew() : null;
        texture = source.ToTextureImage(grContext, mipmapped: !magnified);
        if (sw is not null)
        {
            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds >= GpuUploadTimingLogThresholdMs)
            {
                double mp = source.Width * (double)source.Height / 1_000_000.0;
                RailReaderLogging.Logger.Debug(
                    $"[GPU upload] page {page}: {source.Width}x{source.Height} ({mp:F1} MP), " +
                    $"mipmapped={!magnified}, {sw.Elapsed.TotalMilliseconds:F1} ms");
            }
        }

        old.Texture?.Dispose();
        _gpuTextures[page] = (texture, source);
        return texture;
    }

    private static SKImageFilter? GetCachedBlurFilter(float sigmaX, float sigmaY)
    {
        if (sigmaX <= 0 && sigmaY <= 0) return null;
        if (s_cachedBlurFilter is null
            || Math.Abs(sigmaX - s_cachedSigmaX) > 0.05f
            || Math.Abs(sigmaY - s_cachedSigmaY) > 0.05f)
        {
            s_cachedBlurFilter?.Dispose();
            s_cachedBlurFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY);
            s_cachedSigmaX = sigmaX;
            s_cachedSigmaY = sigmaY;
        }
        return s_cachedBlurFilter;
    }

    private static SKImageFilter? GetCachedNeighborBlurFilter(float sigmaX, float sigmaY)
    {
        if (sigmaX <= 0 && sigmaY <= 0) return null;
        if (s_cachedNeighborBlurFilter is null
            || Math.Abs(sigmaX - s_cachedNeighborSigmaX) > 0.05f
            || Math.Abs(sigmaY - s_cachedNeighborSigmaY) > 0.05f)
        {
            s_cachedNeighborBlurFilter?.Dispose();
            s_cachedNeighborBlurFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY);
            s_cachedNeighborSigmaX = sigmaX;
            s_cachedNeighborSigmaY = sigmaY;
        }
        return s_cachedNeighborBlurFilter;
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
