using RailReader.Core;
using RailReader.Core.Services;
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

    // Continuous-scroll render window: one SKImage wrap per non-anchor visible page, keyed by page
    // number. The anchor's own wrap stays in _cachedImage above (unchanged). Rebuilt each frame from
    // Viewport.VisiblePages by SyncWindowImages, which returns the wraps that fell out of the visible
    // set (page scrolled away, or Core's render window evicted/replaced the underlying bitmap) for the
    // caller to retire on a thread-safe boundary — same contract as GetCachedImage's Retired image.
    private readonly Dictionary<int, (SKImage Image, IRenderedPage Source)> _windowImages = new();

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

    /// <summary>
    /// Wraps every non-anchor entry of <paramref name="visiblePages"/> as an <see cref="SKImage"/>
    /// (skipping the anchor page — <paramref name="anchorPage"/> — which is handled by
    /// <see cref="GetCachedImage"/>, and any entry whose bitmap is still in flight, i.e.
    /// <c>Bitmap: null</c>). Re-wraps only when the underlying bitmap changed (Core's render window
    /// re-rendered it, e.g. a DPI upgrade). Returns the wraps for pages that are no longer in
    /// <paramref name="visiblePages"/> (or whose bitmap changed) so the caller can retire them on a
    /// thread-safe boundary, exactly like <see cref="GetCachedImage"/>'s retired image — the caller
    /// must never dispose these directly on the UI thread while the composition thread might still be
    /// drawing them.
    /// </summary>
    public List<SKImage> SyncWindowImages(IReadOnlyList<VisiblePage> visiblePages, int anchorPage)
    {
        var retired = new List<SKImage>();
        var wanted = new HashSet<int>();
        foreach (var vp in visiblePages)
        {
            if (vp.Page == anchorPage || vp.Bitmap is not SkiaRenderedPage sp) continue;
            wanted.Add(vp.Page);
            if (_windowImages.TryGetValue(vp.Page, out var existing))
            {
                if (ReferenceEquals(existing.Source, sp)) continue;
                retired.Add(existing.Image);
            }
            _windowImages[vp.Page] = (SKImage.FromBitmap(sp.Bitmap), sp);
        }
        List<int>? stale = null;
        foreach (var key in _windowImages.Keys)
            if (!wanted.Contains(key)) (stale ??= new List<int>()).Add(key);
        if (stale is not null)
            foreach (var key in stale)
            {
                retired.Add(_windowImages[key].Image);
                _windowImages.Remove(key);
            }
        return retired;
    }

    /// <summary>The wrapped image for a non-anchor visible page (populated by
    /// <see cref="SyncWindowImages"/>), or null if not currently in the window.</summary>
    public SKImage? GetWindowImage(int page) => _windowImages.TryGetValue(page, out var e) ? e.Image : null;

    /// <summary>
    /// Hands over the wrapped page + minimap <see cref="SKImage"/>s for deferred, thread-safe
    /// disposal and resets this instance to its empty state — WITHOUT disposing them on the calling
    /// (UI) thread. Both wraps are drawn on the composition/render thread (the page via
    /// <c>PdfPageLayer.OnRender</c>, the minimap via its custom draw op), so a direct UI-thread
    /// <c>Dispose()</c> could free one mid-render (railreader2#191). The caller must retire each
    /// returned image on a thread-safe boundary (a <c>RetireImage</c> message to a composition layer).
    /// After this call the instance is empty (re-wraps lazily) and may be reused or dropped.
    /// </summary>
    public List<SKImage> TakeRetiredImages()
    {
        var retired = new List<SKImage>(2 + _windowImages.Count);
        if (_cachedImage is not null) retired.Add(_cachedImage);
        if (_minimapImage is not null) retired.Add(_minimapImage);
        foreach (var (image, _) in _windowImages.Values) retired.Add(image);
        _cachedImage = null;
        _cachedImagePage = null;
        _minimapImage = null;
        _minimapImageSource = null;
        _windowImages.Clear();
        return retired;
    }

    public void Dispose()
    {
        foreach (var (image, _) in _windowImages.Values) image.Dispose();
        _windowImages.Clear();
        _cachedImage?.Dispose();
        _cachedImage = null;
        _cachedImagePage = null;
        _minimapImage?.Dispose();
        _minimapImage = null;
        _minimapImageSource = null;
    }
}
