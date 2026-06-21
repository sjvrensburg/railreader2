using RailReader.Core;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>
/// Per-<see cref="Viewport"/> GPU-image lifecycle: lazily wraps the viewport's rasterised
/// page and minimap thumbnail (Core <see cref="SkiaRenderedPage"/> bitmaps) as <see cref="SKImage"/>s
/// for the composition layers, re-wrapping when the underlying bitmap changes and tracking the
/// retired wrap so the caller can dispose it on a thread-safe boundary.
///
/// <para>Extracted from <see cref="TabViewModel"/> so each viewport of a document (split-pane /
/// tear-off — see <c>docs/multi-viewport-design.md</c>) owns its own page/minimap wraps rather than
/// sharing the document's single Primary wrap. <see cref="TabViewModel"/> holds one of these for its
/// <c>Primary</c> viewport and delegates its image accessors here, so behaviour is unchanged for the
/// single-viewport case. <c>SKImage.FromBitmap</c> must run on the UI thread, which is where all
/// these accessors are called.</para>
/// </summary>
public sealed class ViewportImages : IDisposable
{
    private readonly Viewport _vp;

    private SKImage? _cachedImage;
    private SkiaRenderedPage? _cachedImagePage;
    private SKImage? _minimapImage;
    private SKBitmap? _minimapImageSource;

    public ViewportImages(Viewport vp) => _vp = vp;

    /// <summary>
    /// Returns the cached SKImage for GPU rendering of this viewport's page and the previous
    /// image that was replaced (if any). The caller is responsible for disposing the retired
    /// image on a thread-safe boundary (e.g. the composition thread via OnMessage).
    /// </summary>
    public (SKImage? Current, SKImage? Retired) GetCachedImage()
    {
        if (_vp.CachedPage is SkiaRenderedPage sp)
        {
            if (_cachedImage is null || _cachedImagePage != sp)
            {
                var retired = _cachedImage;
                _cachedImage = SKImage.FromBitmap(sp.Bitmap);
                _cachedImagePage = sp;
                return (_cachedImage, retired);
            }
            return (_cachedImage, null);
        }
        return (null, null);
    }

    /// <summary>
    /// Returns the current cached image without lifecycle management.
    /// Use only when no image transition is expected (e.g. minimap snapshot).
    /// </summary>
    public SKImage? CachedImage => _cachedImage;

    public SKBitmap? MinimapBitmap => (_vp.MinimapPage as SkiaRenderedPage)?.Bitmap;

    /// <summary>
    /// Returns <see cref="MinimapBitmap"/> wrapped as an SKImage so the canvas
    /// can use sampling-aware DrawImage. Re-wraps when the underlying bitmap
    /// changes; previous wrappers are disposed.
    /// </summary>
    public SKImage? MinimapImage
    {
        get
        {
            var bm = MinimapBitmap;
            if (bm is null) return null;
            if (_minimapImageSource is null || !ReferenceEquals(_minimapImageSource, bm))
            {
                _minimapImage?.Dispose();
                _minimapImage = SKImage.FromBitmap(bm);
                _minimapImageSource = bm;
            }
            return _minimapImage;
        }
    }

    public void Dispose()
    {
        _cachedImage?.Dispose();
        _cachedImage = null;
        _cachedImagePage = null;
        _minimapImage?.Dispose();
        _minimapImage = null;
        _minimapImageSource = null;
    }
}
