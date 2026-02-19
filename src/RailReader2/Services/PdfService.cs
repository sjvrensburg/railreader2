using PDFtoImage;
using RailReader2.Models;
using SkiaSharp;

namespace RailReader2.Services;

public sealed class PdfService : IDisposable
{
    private readonly byte[] _pdfBytes;

    public string FilePath { get; }
    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    public PdfService(string filePath)
    {
        FilePath = filePath;
        _pdfBytes = File.ReadAllBytes(filePath);
        PageCount = Conversion.GetPageCount(_pdfBytes);
        Outline = PdfOutlineExtractor.Extract(_pdfBytes);
        if (Outline.Count > 0)
            Console.Error.WriteLine($"[PDF] Extracted {Outline.Count} outline entries");
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        var size = Conversion.GetPageSize(_pdfBytes, page: pageIndex);
        return (size.Width, size.Height);
    }

    /// <summary>
    /// Renders a page to an SKBitmap at the given DPI.
    /// Caller owns the returned bitmap.
    /// </summary>
    public SKBitmap RenderPage(int pageIndex, int dpi = 200)
    {
        return Conversion.ToImage(_pdfBytes, page: pageIndex,
            options: new RenderOptions(Dpi: dpi));
    }

    /// <summary>
    /// Renders a page to RGB bytes at the given target pixel size (for ONNX analysis).
    /// </summary>
    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(targetSize / pageW, targetSize / pageH);
        int pixW = Math.Max(1, (int)(pageW * scale));
        int pixH = Math.Max(1, (int)(pageH * scale));

        using var bitmap = Conversion.ToImage(_pdfBytes, page: pageIndex,
            options: new RenderOptions(Width: pixW, Height: pixH));

        // Convert BGRA (PDFium/SkiaSharp native) to RGB for ONNX
        int actualW = bitmap.Width;
        int actualH = bitmap.Height;
        var pixels = bitmap.GetPixelSpan();
        var rgb = new byte[actualW * actualH * 3];
        int srcIdx = 0, dstIdx = 0;
        for (int i = 0; i < actualW * actualH; i++)
        {
            rgb[dstIdx++] = pixels[srcIdx + 2]; // R (BGRA → RGB)
            rgb[dstIdx++] = pixels[srcIdx + 1]; // G
            rgb[dstIdx++] = pixels[srcIdx];     // B
            srcIdx += 4;
        }

        return (rgb, actualW, actualH);
    }

    /// <summary>
    /// Calculates the appropriate render DPI for the current zoom level.
    /// Higher zoom → higher DPI for sharp text.
    /// </summary>
    public static int CalculateRenderDpi(double zoom)
    {
        // Scale DPI with zoom for sharp text at all zoom levels.
        // At zoom=1 → 150 DPI (overview), zoom=3 → 450 DPI (rail mode sharp text).
        // Cap at 600 DPI to avoid excessively large bitmaps (~35 MP for A4).
        int dpi = Math.Max(150, (int)(zoom * 150));
        return Math.Min(dpi, 600);
    }

    public void Dispose() { }
}
