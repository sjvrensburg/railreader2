using PDFtoImage;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Renderer.Skia;

/// <summary>
/// PDFium/SkiaSharp implementation of IPdfService.
/// </summary>
public sealed class SkiaPdfService : IPdfService
{
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    public byte[] PdfBytes { get; }
    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    public SkiaPdfService(string filePath)
    {
        PdfBytes = File.ReadAllBytes(filePath);
        lock (PdfiumGate.Lock)
        {
            PageCount = Conversion.GetPageCount(PdfBytes);
            Outline = PdfOutlineExtractor.Extract(PdfBytes);
        }
        if (Outline.Count > 0)
            Logger.Debug($"[PDF] Extracted {Outline.Count} outline entries");
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (PdfiumGate.Lock)
        {
            var size = Conversion.GetPageSize(PdfBytes, page: pageIndex);
            return (size.Width, size.Height);
        }
    }

    public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
    {
        lock (PdfiumGate.Lock)
        {
            var bitmap = Conversion.ToImage(PdfBytes, page: pageIndex,
                options: new RenderOptions(Dpi: dpi));
            return new SkiaRenderedPage(bitmap);
        }
    }

    public IRenderedPage RenderThumbnail(int pageIndex)
    {
        lock (PdfiumGate.Lock)
        {
            var (pixW, pixH) = FitPageToTarget(pageIndex, 200);
            var bitmap = Conversion.ToImage(PdfBytes, page: pageIndex,
                options: new RenderOptions(Width: pixW, Height: pixH));
            return new SkiaRenderedPage(bitmap);
        }
    }

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
    {
        lock (PdfiumGate.Lock)
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
    }

    private (int Width, int Height) FitPageToTarget(int pageIndex, int targetSize)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(targetSize / pageW, targetSize / pageH);
        return (Math.Max(1, (int)(pageW * scale)), Math.Max(1, (int)(pageH * scale)));
    }

}
