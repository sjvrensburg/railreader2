using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Wraps an SKBitmap as an IRenderedPage. Exposes the bitmap for callers
/// that need the underlying Skia type (e.g. GPU image upload).
/// </summary>
public sealed class SkiaRenderedPage : IRenderedPage
{
    public SKBitmap Bitmap { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public SkiaRenderedPage(SKBitmap bitmap) => Bitmap = bitmap;

    public void Dispose() => Bitmap.Dispose();
}
