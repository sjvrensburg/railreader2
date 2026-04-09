using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Renders a layout block region from a PDF page as PNG bytes.
/// </summary>
public static class BlockCropRenderer
{
    /// <summary>
    /// Renders the page at the given DPI, crops to the block's BBox with padding,
    /// and returns the result as PNG bytes.
    /// </summary>
    public static byte[]? RenderBlockAsPng(IPdfService pdf, int pageIndex,
        BBox blockBBox, double pageWidth, double pageHeight, int dpi = 300)
    {
        using var rendered = pdf.RenderPage(pageIndex, dpi);
        if (rendered is not SkiaRenderedPage skiaPage) return null;

        var bitmap = skiaPage.Bitmap;
        float scaleX = bitmap.Width / (float)pageWidth;
        float scaleY = bitmap.Height / (float)pageHeight;

        // Add ~5% padding around the block for context
        float padX = blockBBox.W * 0.05f;
        float padY = blockBBox.H * 0.05f;

        var cropRect = new SKRectI(
            Math.Max(0, (int)((blockBBox.X - padX) * scaleX)),
            Math.Max(0, (int)((blockBBox.Y - padY) * scaleY)),
            Math.Min(bitmap.Width, (int)((blockBBox.X + blockBBox.W + padX) * scaleX)),
            Math.Min(bitmap.Height, (int)((blockBBox.Y + blockBBox.H + padY) * scaleY)));

        if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

        using var cropped = new SKBitmap();
        if (!bitmap.ExtractSubset(cropped, cropRect)) return null;

        using var data = cropped.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
