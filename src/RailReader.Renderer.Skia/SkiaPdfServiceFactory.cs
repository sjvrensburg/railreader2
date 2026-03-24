using RailReader.Core.Services;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Factory that creates PDFium/SkiaSharp PDF service implementations.
/// Ensures PdfiumResolver is initialized before any PDFium calls.
/// </summary>
public sealed class SkiaPdfServiceFactory : IPdfServiceFactory
{
    private static bool s_resolverInitialized;

    public SkiaPdfServiceFactory()
    {
        if (!s_resolverInitialized)
        {
            PdfiumResolver.Initialize();
            s_resolverInitialized = true;
        }
    }

    public IPdfService CreatePdfService(string filePath)
        => new SkiaPdfService(filePath);

    public IPdfTextService CreatePdfTextService()
        => new SkiaPdfTextService();
}
