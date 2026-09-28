using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.Services;
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
    // sustained ~1.5s scroll/zoom hold). 6.0f made it clearly visible but assumed users crank the
    // intensity slider to max; settled on 4.5f (~1.5px at default intensity) since intensity 1.0 is an
    // edge case, not the expected usage.
    private const float MotionBlurMaxDeviceSigma = 4.5f;

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

    // Use the trilinear (mipmapped) sampler during motion only when the on-screen footprint is below
    // this fraction of the source image's width. Core's DPI tiers keep a rail page within roughly ±15% of
    // 1:1 (or magnified above the DPI cap), so rail reading stays on Mitchell throughout; a zoom
    // animation well below the current tier still gets the alias-free path.
    private const float FastSamplingMinifyFactor = 0.75f;

    // Texture tiling (see _pageTextures). 512 px tiles keep any one upload to ~0.3 MP; the gutter is
    // aligned to 16 so tile mip grids line up with the whole image's.
    private const int TileSize = 512;
    private const int TileGutter = 16;
    // CPU time per frame (this layer's OnRender, drawing included) after which no further tile is
    // uploaded that frame — the first upload of a frame is always allowed.
    private const double UploadBudgetMs = 4.0;

    // Diagnostic-only: times each frame's tile uploads and logs the frame's total when it exceeds
    // GpuUploadTimingLogThresholdMs. Off by default — gated behind RR_GPU_UPLOAD_TIMING=1 so we never
    // pay a synchronous, flushing ConsoleLogger write on the composition thread in normal use (#222
    // Phase 1). Parsed once; the field itself is the only per-frame cost when disabled.
    private static readonly bool s_gpuUploadTimingEnabled =
        Environment.GetEnvironmentVariable("RR_GPU_UPLOAD_TIMING") == "1";
    private const double GpuUploadTimingLogThresholdMs = 3.0;

    // Diagnostic-only per-frame draw timing, shared with the frame loop's RR_FRAME_TIMING=1 switch:
    // logs this layer's CPU time (a GPU upload shows up here), the anchor's texture/scale/sampler and
    // Skia's GPU resource-cache usage per frame, batched into one log write per 120 frames.
    private static readonly bool s_drawTiming =
        Environment.GetEnvironmentVariable("RR_FRAME_TIMING") == "1";
    private readonly System.Text.StringBuilder _drawTimingBuffer = new();
    private int _drawTimingLines;
    private string _drawTimingAnchor = "";

    // ThreadStatic caches: one per composition thread (typically one per renderer)
    [ThreadStatic] private static SKPaint? s_imagePaint;
    [ThreadStatic] private static SKPaint? s_layerPaint;
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

    // Mitchell cubic for crisp text; trilinear only for a texture that is being clearly MINIFIED while
    // the camera moves (low-zoom zoom animation), where the mip chain eliminates texel-hop aliasing that
    // cubic sampling would show. Choosing by motion alone flipped a 1:1/magnified rail page between the
    // two filters at every auto-scroll line start and stop — a visible sharpness "pop" twice per line
    // that read as stutter, and more so the more the texture is magnified.
    private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);
    private static readonly SKSamplingOptions s_samplingFast =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private PdfPageRenderState? _state;

    // GPU texture cache, one entry per currently-visible page (single-page mode: just the anchor).
    // A page's source image — a raster SKImage from ViewportImages, often ~25 MP at reading DPI — is
    // uploaded as a grid of TileSize tiles, and only the tiles on screen plus a prefetch ring around
    // them (#222 Phase 3's margin, now filled incrementally) are ever resident.
    //
    // Why tiles: the previous single sub-rect texture (visible rect padded by a full viewport on every
    // side, 9x the visible area) had to be re-uploaded in one go whenever the view escaped it, and at a
    // full-screen viewport that was 13-22 MP mipmapped per upload — a 260-610 ms freeze mid-scroll on
    // either GPU (Ganesh builds the mip chain of a raster upload on the CPU). Tiles bound the cost of
    // any single upload, let the ring fill a few milliseconds per frame ahead of travel, and make a
    // required upload only ever the newly revealed strip.
    //
    // Each tile is uploaded with a TileGutter-pixel border of its neighbours' real pixels and drawn
    // with a source rect of just its own content: SkiaSharp's DrawImage(image, src, dst, ...) uses
    // Skia's kFast src-rect constraint (checked: linear filtering reads texels outside src), so filter
    // taps at a tile edge see the neighbouring pixels and adjacent tiles join without a seam. 16 px of
    // gutter also covers the mip levels trilinear sampling uses during a zoomed-out zoom animation,
    // and keeps each tile's mip grid aligned with the whole image's down to level 4.
    //
    // Lifetime: ToTextureImage's upload from a raster subset is not fully resolved when the call
    // returns — live-tested twice via gdb: disposing the subset right after the call segfaults
    // (sk_image_get_width) on a later frame, and separately RetireImage disposing the subset's source
    // once a newer DPI tier replaces it crashed the same way. So every tile keeps its Subset alive for
    // as long as its Texture, and a TileSet keeps its Source reference; a flush after a frame's
    // uploads (see OnRender) only SUBMITS the work, it is not a completion guarantee.
    //
    // A DPI-tier change swaps in a new TileSet and keeps the old one as Previous, drawn underneath
    // until the new set's visible tiles are all resident, so a tier upgrade fills in over a few frames
    // instead of stalling one. Pruned in OnMessage when a page leaves the visible set.
    private readonly Dictionary<int, PageTextures> _pageTextures = new();

    private readonly record struct Tile(SKImage Texture, SKImage Subset);

    /// <summary>Resident tiles of one source image. <see cref="Source"/> is only ever compared by
    /// reference: RetireImage can dispose it while the set is still drawn as a Previous fallback, and
    /// reading Width/Height through a disposed SKImage dereferences a freed native handle — hence the
    /// size captured at construction.</summary>
    private sealed class TileSet(SKImage source)
    {
        public SKImage Source { get; } = source;
        public int SourceWidth { get; } = source.Width;
        public int SourceHeight { get; } = source.Height;
        public Dictionary<(int X, int Y), Tile> Tiles { get; } = new();

        public void Dispose()
        {
            foreach (var tile in Tiles.Values)
            {
                tile.Texture.Dispose();
                tile.Subset.Dispose();
            }
            Tiles.Clear();
        }
    }

    private sealed class PageTextures
    {
        public TileSet? Current;
        public TileSet? Previous;

        public void Dispose()
        {
            Current?.Dispose();
            Previous?.Dispose();
        }
    }

    /// <summary>Inclusive tile-index range.</summary>
    private readonly record struct TileRange(int X0, int Y0, int X1, int Y1);

    /// <summary>A page's prefetch work, queued while drawing and done after every page has drawn.</summary>
    private readonly record struct PrefetchJob(int Page, PageTextures Textures, SKRectI Required, bool Mipmapped);

    private readonly List<PrefetchJob> _prefetchJobs = new();
    private readonly List<(int X, int Y)> _tileScratch = new();

    // Per-frame upload accounting (OnRender only, composition thread).
    private long _frameStart;
    private int _frameUploads;
    private double _frameUploadMs;
    private double _frameUploadMp;

    public override void OnMessage(object message)
    {
        if (message is RetireImage retire)
        {
            // Dispose the old SKImage on the composition thread where we
            // know OnRender is not concurrently accessing it.
            retire.Image.Dispose();
            return;
        }

        if (message is ReleaseResources)
        {
            // Disposing a texture-backed SKImage only drops Skia's reference (the texture becomes
            // purgeable and is freed at a later flush with the context current), so this is safe here.
            foreach (var textures in _pageTextures.Values) textures.Dispose();
            _pageTextures.Clear();
            _state = null;
            return;
        }

        if (message is PdfPageRenderState state)
        {
            if (_pageTextures.Count > 0)
            {
                var wanted = new HashSet<int>();
                foreach (var p in state.Pages) wanted.Add(p.Page);
                List<int>? stale = null;
                foreach (var key in _pageTextures.Keys)
                    if (!wanted.Contains(key)) (stale ??= new List<int>()).Add(key);
                if (stale is not null)
                    foreach (var key in stale)
                    {
                        _pageTextures[key].Dispose();
                        _pageTextures.Remove(key);
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
        if (grContext?.Backend == GRBackend.OpenGL) DisplayGpu.ProbeRenderer();
        _frameStart = Stopwatch.GetTimestamp();
        _frameUploads = 0;
        _frameUploadMs = 0;
        _frameUploadMp = 0;
        _drawTimingAnchor = "none";

        bool animating = state.ScrollSpeed > MinSpeedThreshold || state.ZoomSpeed > MinSpeedThreshold;

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
        // rather than stacking two blur passes.
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

        // A page left incomplete this frame (visible tiles deferred to a later frame) or a prefetch
        // ring not yet filled schedules another frame, so outstanding uploads drain a budget's worth
        // per frame. Terminates: every such frame uploads at least one tile (CanUpload).
        bool moreWork = false;
        _prefetchJobs.Clear();

        foreach (var page in state.Pages)
        {
            if (page.Image is null) continue; // render in flight — the panel's own background is the gap colour

            var image = page.Image;
            float deviceWidth = page.PageW * canvas.TotalMatrix.ScaleX * state.Zoom;

            canvas.Save();
            canvas.Concat(page.Camera);

            var pageRect = SKRect.Create(0, 0, page.PageW, page.PageH);
            var blurFilter = page.IsAnchor ? motionBlurFilter : neighborBlurFilter;
            // Minification is a pixel-density comparison against the FULL source width, so it holds
            // for tiles as much as for the whole image.
            bool minified = deviceWidth < image.Width * FastSamplingMinifyFactor;
            bool fastSampling = animating && minified;
            var sampling = fastSampling ? s_samplingFast : s_sampling;

            // Colour effect goes on each image draw (per-pixel, so tiling can't show); blur goes on a
            // layer around all of this page's draws, because blurring each tile separately would fade
            // every tile's edges to transparent — a visible grid during motion. Skia already renders an
            // image-filtered DrawImage through an internal layer of the same size, so the explicit
            // layer costs nothing extra over the old single-image path.
            SKPaint? imagePaint = null;
            if (effectFilter is not null)
            {
                s_imagePaint ??= new SKPaint();
                s_imagePaint.ColorFilter = effectFilter;
                imagePaint = s_imagePaint;
            }
            if (blurFilter is not null)
            {
                s_layerPaint ??= new SKPaint();
                s_layerPaint.ImageFilter = blurFilter;
                canvas.SaveLayer(pageRect, s_layerPaint);
            }

            if (grContext is null || image.Width <= 0 || image.Height <= 0 || page.PageW <= 0f || page.PageH <= 0f)
            {
                // No GPU context available (e.g. software fallback) — draw the raster source directly;
                // texture caching/tiling doesn't apply. Deliberately does NOT fall back to previously
                // uploaded tiles still resident for this page: a texture created under a GPU context can
                // be invalidated by the very loss of that context, so the raw raster is the safe choice.
                //
                // The same fallback covers degenerate page/image geometry (e.g. a corrupt PDF page whose
                // FPDF_LoadPage failed, leaving PageW/PageH at 0 while a bitmap still renders): the
                // page-to-image scale math below divides by PageW/PageH and feeds the result to
                // SKImage.Subset, which segfaults natively on a zero-area rect rather than throwing.
                canvas.DrawImage(image, SKRect.Create(image.Width, image.Height), pageRect, sampling, imagePaint);
                if (s_drawTiming && page.IsAnchor)
                    _drawTimingAnchor = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"raster src={image.Width}x{image.Height} scale={deviceWidth / image.Width:F2} {(fastSampling ? "trilinear" : "mitchell")}");
            }
            else
            {
                // Map the visible clip — in LOCAL (page) space, i.e. AFTER the camera concat above — to
                // source-image pixels. Intersect with the page first: a viewport larger than the page, or
                // a camera not yet settled this frame, must not inflate the required rect past it.
                var visiblePageRect = canvas.LocalClipBounds;
                visiblePageRect.Intersect(pageRect);
                if (visiblePageRect.IsEmpty) visiblePageRect = pageRect;

                var textures = GetPageTextures(page.Page, image);
                var current = textures.Current!;
                var required = PageToImageRect(visiblePageRect, current, page.PageW, page.PageH);
                var range = TilesFor(required);
                bool mipmapped = !(deviceWidth > image.Width * MipmapSkipMagnifyFactor);

                // The previous DPI tier's tiles can stand in for missing ones, but only if they cover
                // the whole visible rect — otherwise a deferred tile would show as a hole.
                var previous = textures.Previous;
                TileRange previousRange = default;
                bool previousCovers = false;
                if (previous is not null)
                {
                    previousRange = TilesFor(PageToImageRect(visiblePageRect, previous, page.PageW, page.PageH));
                    previousCovers = AllResident(previous, previousRange);
                }

                // Visible tiles. The anchor is what the user is reading: with no complete fallback its
                // missing tiles upload now whatever the budget. Anything else (a fallback-covered anchor
                // after a tier change, a neighbouring page in continuous scroll) waits for budget.
                bool complete = true;
                int visibleTiles = 0, residentTiles = 0;
                for (int ty = range.Y0; ty <= range.Y1; ty++)
                for (int tx = range.X0; tx <= range.X1; tx++)
                {
                    visibleTiles++;
                    if (!current.Tiles.ContainsKey((tx, ty)))
                    {
                        bool mustUpload = page.IsAnchor && !previousCovers;
                        if (!((mustUpload || CanUpload()) && TryUploadTile(page.Page, current, tx, ty, grContext, mipmapped)))
                        {
                            complete = false;
                            continue;
                        }
                    }
                    residentTiles++;
                }

                if (!complete && previous is not null)
                    DrawTiles(canvas, previous, previousRange, page.PageW, page.PageH, sampling, imagePaint);
                DrawTiles(canvas, current, range, page.PageW, page.PageH, sampling, imagePaint);

                if (complete && previous is not null)
                {
                    previous.Dispose();
                    textures.Previous = null;
                }
                if (!complete) moreWork = true;

                var job = new PrefetchJob(page.Page, textures, required, mipmapped);
                if (page.IsAnchor) _prefetchJobs.Insert(0, job);
                else _prefetchJobs.Add(job);

                if (s_drawTiming && page.IsAnchor)
                    _drawTimingAnchor = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"tiles={residentTiles}/{visibleTiles}{(!complete && previous is not null ? "+prev" : "")} resident={current.Tiles.Count} src={image.Width}x{image.Height} scale={deviceWidth / image.Width:F2} {(fastSampling ? "trilinear" : "mitchell")}");
            }

            if (blurFilter is not null)
            {
                canvas.Restore(); // apply the blur layer
                s_layerPaint!.ImageFilter = null;
            }
            // Don't let the cached paint retain a ref to a filter that may be disposed (effect/intensity
            // change) before the next frame reassigns it.
            if (imagePaint is not null) imagePaint.ColorFilter = null;

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

                canvas.DrawRect(pageRect, s_cachedDimPaint);
            }

            canvas.Restore(); // undo camera concat
        }

        // Prefetch after drawing, so it only spends whatever budget the visible tiles left over.
        if (grContext is not null)
            foreach (var job in _prefetchJobs)
                if (!Prefetch(job, grContext)) moreWork = true;
        _prefetchJobs.Clear();

        // Submit this frame's uploads promptly rather than leaving them batched behind whatever Skia
        // would coalesce them with. Not a completion guarantee — see the lifetime note on _pageTextures.
        if (_frameUploads > 0)
        {
            grContext?.Flush(submit: true, synchronous: false);
            if (s_gpuUploadTimingEnabled && _frameUploadMs >= GpuUploadTimingLogThresholdMs)
                RailReaderLogging.Logger.Debug(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[GPU upload] {_frameUploads} tile(s), {_frameUploadMp:F2} MP, {_frameUploadMs:F1}ms"));
        }

        if (s_drawTiming)
            LogDrawTiming(grContext, animating, motionBlurFilter is not null);

        if (moreWork) Invalidate();
    }

    /// <summary>The page's texture cache, switched to a new <see cref="TileSet"/> when its source image
    /// changed (DPI tier). The outgoing set is kept as the fallback unless it has nothing to offer.</summary>
    private PageTextures GetPageTextures(int page, SKImage image)
    {
        if (!_pageTextures.TryGetValue(page, out var textures))
            _pageTextures[page] = textures = new PageTextures();

        if (textures.Current is null || !ReferenceEquals(textures.Current.Source, image))
        {
            if (textures.Current is { Tiles.Count: > 0 })
            {
                textures.Previous?.Dispose();
                textures.Previous = textures.Current;
            }
            else
            {
                textures.Current?.Dispose();
            }
            textures.Current = new TileSet(image);
        }
        return textures;
    }

    /// <summary>Uploads tiles in the ring around the visible rect (a full visible extent on each side —
    /// what #222's single padded upload covered), nearest first, while budget remains; evicts tiles
    /// that have drifted well outside it. Returns false while the ring still has missing tiles.</summary>
    private bool Prefetch(PrefetchJob job, GRContext grContext)
    {
        var set = job.Textures.Current!;
        var bounds = SKRectI.Create(set.SourceWidth, set.SourceHeight);
        var req = job.Required;

        // Eviction uses a wider rect than the ring, so tiles at the ring's edge don't churn.
        var keep = TilesFor(SKRectI.Intersect(
            SKRectI.Inflate(req, req.Width * 3 / 2 + TileSize, req.Height * 3 / 2 + TileSize), bounds));
        _tileScratch.Clear();
        foreach (var key in set.Tiles.Keys)
            if (key.X < keep.X0 || key.X > keep.X1 || key.Y < keep.Y0 || key.Y > keep.Y1)
                _tileScratch.Add(key);
        foreach (var key in _tileScratch)
        {
            var tile = set.Tiles[key];
            tile.Texture.Dispose();
            tile.Subset.Dispose();
            set.Tiles.Remove(key);
        }

        var ring = TilesFor(SKRectI.Intersect(SKRectI.Inflate(req, req.Width, req.Height), bounds));
        _tileScratch.Clear();
        for (int ty = ring.Y0; ty <= ring.Y1; ty++)
        for (int tx = ring.X0; tx <= ring.X1; tx++)
            if (!set.Tiles.ContainsKey((tx, ty))) _tileScratch.Add((tx, ty));
        if (_tileScratch.Count == 0) return true;

        float cx = req.MidX / (float)TileSize - 0.5f, cy = req.MidY / (float)TileSize - 0.5f;
        _tileScratch.Sort((a, b) =>
            ((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy))
            .CompareTo((b.X - cx) * (b.X - cx) + (b.Y - cy) * (b.Y - cy)));

        foreach (var (tx, ty) in _tileScratch)
        {
            if (!CanUpload()) return false;
            if (!TryUploadTile(job.Page, set, tx, ty, grContext, job.Mipmapped)) return false;
        }
        return true;
    }

    /// <summary>Whether another tile may be uploaded this frame: always the first (so outstanding work
    /// makes progress however slow the frame), then while the frame's CPU time is under budget.</summary>
    private bool CanUpload()
        => _frameUploads == 0 || Stopwatch.GetElapsedTime(_frameStart).TotalMilliseconds < UploadBudgetMs;

    private bool TryUploadTile(int page, TileSet set, int tx, int ty, GRContext grContext, bool mipmapped)
    {
        var content = TileContent(tx, ty, set.SourceWidth, set.SourceHeight);
        var uploadRect = TileUploadRect(content, set.SourceWidth, set.SourceHeight);
        long start = Stopwatch.GetTimestamp();

        // A cheap shared-pixel view of the source (Core marks rendered page bitmaps immutable), so only
        // the tile's own pixels are uploaded. set.Source is this frame's image here — prefetch and
        // visible uploads only ever target a page's Current set, never a retired Previous.
        var subset = set.Source.Subset(uploadRect);
        if (subset is null)
        {
            // Observed only in theory (a native Skia edge case), never live. Treat as a deferred tile;
            // the next frame retries.
            RailReaderLogging.Logger.Error(
                $"[GPU upload] SKImage.Subset returned null for page {page}, rect {uploadRect} — skipping this tile.");
            return false;
        }

        SKImage? texture;
        try
        {
            texture = subset.ToTextureImage(grContext, mipmapped);
        }
        catch (Exception ex)
        {
            // e.g. GPU OOM. The subset is fresh and referenced by nothing else, so dispose it; the tile
            // stays missing and is retried on a later frame while the rest of the page keeps drawing.
            subset.Dispose();
            RailReaderLogging.Logger.Error($"[GPU upload] tile ({tx},{ty}) of page {page} failed", ex);
            return false;
        }
        if (texture is null)
        {
            subset.Dispose();
            return false;
        }

        set.Tiles[(tx, ty)] = new Tile(texture, subset);
        _frameUploads++;
        _frameUploadMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _frameUploadMp += uploadRect.Width * (double)uploadRect.Height / 1_000_000.0;
        return true;
    }

    private static void DrawTiles(SKCanvas canvas, TileSet set, TileRange range, float pageW, float pageH,
        SKSamplingOptions sampling, SKPaint? paint)
    {
        // Page-point size doesn't change with DPI tier, so each set maps through its OWN source scale —
        // a Previous set from another tier still lands at the geometrically correct place.
        float scaleX = set.SourceWidth / pageW;
        float scaleY = set.SourceHeight / pageH;
        for (int ty = range.Y0; ty <= range.Y1; ty++)
        for (int tx = range.X0; tx <= range.X1; tx++)
        {
            if (!set.Tiles.TryGetValue((tx, ty), out var tile)) continue;
            var content = TileContent(tx, ty, set.SourceWidth, set.SourceHeight);
            var uploadRect = TileUploadRect(content, set.SourceWidth, set.SourceHeight);
            var src = SKRect.Create(content.Left - uploadRect.Left, content.Top - uploadRect.Top,
                content.Width, content.Height);
            canvas.DrawImage(tile.Texture, src, MapImageRectToPage(content, scaleX, scaleY), sampling, paint);
        }
    }

    private static bool AllResident(TileSet set, TileRange range)
    {
        for (int ty = range.Y0; ty <= range.Y1; ty++)
        for (int tx = range.X0; tx <= range.X1; tx++)
            if (!set.Tiles.ContainsKey((tx, ty))) return false;
        return true;
    }

    /// <summary>Maps a page-space rect to the (outward-rounded, clamped) source-pixel rect of
    /// <paramref name="set"/>. A degenerate result — e.g. the very first frame, before the camera and
    /// layout have settled — falls back to the whole image: native Skia doesn't validate a zero-area
    /// subset and segfaults rather than throwing (observed live).</summary>
    private static SKRectI PageToImageRect(SKRect pageRect, TileSet set, float pageW, float pageH)
    {
        float scaleX = set.SourceWidth / pageW;
        float scaleY = set.SourceHeight / pageH;
        var bounds = SKRectI.Create(set.SourceWidth, set.SourceHeight);
        var imageRect = new SKRect(pageRect.Left * scaleX, pageRect.Top * scaleY,
            pageRect.Right * scaleX, pageRect.Bottom * scaleY);
        var result = SKRectI.Intersect(SKRectI.Ceiling(imageRect, outwards: true), bounds);
        return result.Width <= 0 || result.Height <= 0 ? bounds : result;
    }

    private static TileRange TilesFor(SKRectI rect)
        => new(rect.Left / TileSize, rect.Top / TileSize, (rect.Right - 1) / TileSize, (rect.Bottom - 1) / TileSize);

    private static SKRectI TileContent(int tx, int ty, int width, int height)
        => SKRectI.Intersect(SKRectI.Create(tx * TileSize, ty * TileSize, TileSize, TileSize),
            SKRectI.Create(width, height));

    private static SKRectI TileUploadRect(SKRectI content, int width, int height)
        => SKRectI.Intersect(SKRectI.Inflate(content, TileGutter, TileGutter), SKRectI.Create(width, height));

    /// <summary>Maps a rect in source-image pixel coordinates back to page-space coordinates, the
    /// inverse of the page-to-image scale used to compute it.</summary>
    private static SKRect MapImageRectToPage(SKRectI imageRect, float scaleX, float scaleY)
        => SKRect.Create(imageRect.Left / scaleX, imageRect.Top / scaleY,
            imageRect.Width / scaleX, imageRect.Height / scaleY);

    private void LogDrawTiming(GRContext? grContext, bool animating, bool blur)
    {
        double cpuMs = Stopwatch.GetElapsedTime(_frameStart).TotalMilliseconds;
        long cacheBytes = 0, cacheLimit = 0;
        int cacheCount = 0;
        if (grContext is not null)
        {
            grContext.GetResourceCacheUsage(out cacheCount, out cacheBytes);
            cacheLimit = grContext.GetResourceCacheLimit();
        }
        _drawTimingBuffer.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"[draw] cpu={cpuMs:F2}ms up={_frameUploads}/{_frameUploadMs:F1}ms anchor={_drawTimingAnchor} animating={animating} blur={blur} cache={cacheBytes / 1048576.0:F1}/{cacheLimit / 1048576.0:F0}MB n={cacheCount}\n");
        if (++_drawTimingLines >= 120)
        {
            RailReaderLogging.Logger.Debug(_drawTimingBuffer.ToString().TrimEnd('\n'));
            _drawTimingBuffer.Clear();
            _drawTimingLines = 0;
        }
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
