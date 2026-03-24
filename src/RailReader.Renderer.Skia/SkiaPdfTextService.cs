using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Renderer.Skia;

/// <summary>
/// PDFium-based implementation of IPdfTextService.
/// Delegates to the existing static PdfTextService in Core (which still has the PDFium P/Invoke code).
/// </summary>
public sealed class SkiaPdfTextService : IPdfTextService
{
    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex)
        => PdfTextService.ExtractPageText(pdfBytes, pageIndex);

    public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges)
        => PdfTextService.GetTextRangeRects(pdfBytes, pageIndex, ranges);
}
