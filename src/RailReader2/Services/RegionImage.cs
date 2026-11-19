using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader2.Services;

/// <summary>
/// Renders one rectangle of a page through Core's region rendering (RailReaderCore#222), which
/// rasterises only that rectangle instead of the whole page at the same scale. Used by the crop
/// paths: portal previews, link peeks, frozen panes. The pixels match a crop of a whole-page render
/// (both leave annotations out, as <c>RenderPage</c> does). Callers must be on a thread allowed to
/// call PDFium through Core (it takes <c>PdfiumGate.Lock</c> itself).
/// </summary>
internal static class RegionImage
{
    /// <summary>Render <paramref name="region"/> (displayed page points, in the
    /// <paramref name="viewRotation"/> frame) at <paramref name="dpi"/> as an opaque BGRA
    /// <see cref="SKBitmap"/> the caller owns. The bitmap covers the region clamped to the page and
    /// snapped outward to whole pixels. Null when the region misses the page.</summary>
    public static SKBitmap? RenderBitmap(IPdfService pdf, int page, BBox region, double dpi, int viewRotation = 0)
    {
        if (Render(pdf, page, region, dpi, viewRotation) is not { } r) return null;
        var (rgb, width, height) = r;
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        WriteBgra(rgb, width, height, bitmap.GetPixels(), bitmap.RowBytes);
        return bitmap;
    }

    /// <summary>As <see cref="RenderBitmap"/>, straight into an Avalonia <see cref="Bitmap"/>
    /// (no PNG round-trip).</summary>
    public static Bitmap? RenderAvaloniaBitmap(IPdfService pdf, int page, BBox region, double dpi, int viewRotation = 0)
    {
        if (Render(pdf, page, region, dpi, viewRotation) is not { } r) return null;
        var (rgb, width, height) = r;
        var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
            WriteBgra(rgb, width, height, fb.Address, fb.RowBytes);
        return wb;
    }

    private static (byte[] Rgb, int Width, int Height)? Render(IPdfService pdf, int page, BBox region,
        double dpi, int viewRotation)
    {
        if (!(region.W > 0) || !(region.H > 0)) return null;
        try
        {
            return pdf.RenderRegionPixmap(page, region, dpi / 72.0, viewRotation);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // the region misses the page
        }
    }

    /// <summary>Packed RGB (3 bytes per pixel, unpadded rows) → opaque BGRA rows at
    /// <paramref name="dest"/>.</summary>
    private static void WriteBgra(byte[] rgb, int width, int height, IntPtr dest, int destRowBytes)
    {
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            int src = y * width * 3;
            for (int x = 0, d = 0; x < width; x++, src += 3, d += 4)
            {
                row[d] = rgb[src + 2];
                row[d + 1] = rgb[src + 1];
                row[d + 2] = rgb[src];
                row[d + 3] = 255;
            }
            Marshal.Copy(row, 0, dest + y * destRowBytes, row.Length);
        }
    }
}
