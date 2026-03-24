using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Opaque handle to a rendered page. Platform implementations wrap their native
/// bitmap type (e.g. SKBitmap, CGImage).
/// </summary>
public interface IRenderedPage : IDisposable
{
    int Width { get; }
    int Height { get; }
}


/// <summary>
/// Rendering-library-agnostic PDF service.
/// </summary>
public interface IPdfService
{
    byte[] PdfBytes { get; }
    int PageCount { get; }
    List<OutlineEntry> Outline { get; }

    (double Width, double Height) GetPageSize(int pageIndex);
    IRenderedPage RenderPage(int pageIndex, int dpi = 200);
    IRenderedPage RenderThumbnail(int pageIndex);

    /// <summary>
    /// Renders a page to RGB bytes at the given target pixel size (for ONNX analysis).
    /// </summary>
    (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize);
}
