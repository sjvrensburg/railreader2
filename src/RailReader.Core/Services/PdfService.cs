using PDFtoImage;
using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

public sealed class PdfService
{
    public byte[] PdfBytes { get; }
    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    public PdfService(string filePath)
    {
        PdfBytes = File.ReadAllBytes(filePath);
        PageCount = Conversion.GetPageCount(PdfBytes);
        Outline = PdfOutlineExtractor.Extract(PdfBytes);
#if DEBUG
        if (Outline.Count > 0)
            Console.Error.WriteLine($"[PDF] Extracted {Outline.Count} outline entries");
#endif
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        var size = Conversion.GetPageSize(PdfBytes, page: pageIndex);
        return (size.Width, size.Height);
    }

    /// <summary>
    /// Renders a page to an SKBitmap at the given DPI.
    /// Caller owns the returned bitmap.
    /// </summary>
    public SKBitmap RenderPage(int pageIndex, int dpi = 200)
    {
        return Conversion.ToImage(PdfBytes, page: pageIndex,
            options: new RenderOptions(Dpi: dpi));
    }

    /// <summary>
    /// Renders a page to RGB bytes at the given target pixel size (for ONNX analysis).
    /// </summary>
    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
    {
        var (pixW, pixH) = FitPageToTarget(pageIndex, targetSize);

        using var bitmap = Conversion.ToImage(PdfBytes, page: pageIndex,
            options: new RenderOptions(Width: pixW, Height: pixH));

        // Convert BGRA (PDFium/SkiaSharp native) to RGB for ONNX
        var pixels = bitmap.GetPixelSpan();
        int pixelCount = bitmap.Width * bitmap.Height;
        var rgb = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            int src = i * 4;
            int dst = i * 3;
            rgb[dst] = pixels[src + 2];     // R (BGRA -> RGB)
            rgb[dst + 1] = pixels[src + 1]; // G
            rgb[dst + 2] = pixels[src];     // B
        }

        return (rgb, bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// Renders a small thumbnail of a page suitable for the minimap (≤200×280 px).
    /// Caller owns the returned bitmap.
    /// </summary>
    public SKBitmap RenderThumbnail(int pageIndex)
    {
        var (pixW, pixH) = FitPageToTarget(pageIndex, 200);
        return Conversion.ToImage(PdfBytes, page: pageIndex,
            options: new RenderOptions(Width: pixW, Height: pixH));
    }

    private (int Width, int Height) FitPageToTarget(int pageIndex, int targetSize)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(targetSize / pageW, targetSize / pageH);
        return (Math.Max(1, (int)(pageW * scale)), Math.Max(1, (int)(pageH * scale)));
    }

    /// <summary>
    /// Calculates the appropriate render DPI for the current zoom level.
    /// Higher zoom → higher DPI for sharp text.
    /// </summary>
    public static int CalculateRenderDpi(double zoom)
    {
        int raw = (int)(zoom * 150);
        int rounded = ((raw + 37) / 75) * 75; // snap to nearest 75 DPI step
        return Math.Clamp(rounded, 150, 600);
    }

}
