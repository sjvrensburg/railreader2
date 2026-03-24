using RailReader.Core.Services;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Factory that creates PDFium/SkiaSharp PDF service implementations.
/// Ensures PdfiumResolver is initialized before any PDFium calls.
/// </summary>
public sealed class SkiaPdfServiceFactory : IPdfServiceFactory
{
    public SkiaPdfServiceFactory()
    {
        PdfiumResolver.Initialize();
    }

    public IPdfService CreatePdfService(string filePath)
        => new SkiaPdfService(filePath);

    public IPdfTextService CreatePdfTextService()
        => new SkiaPdfTextService();
}
