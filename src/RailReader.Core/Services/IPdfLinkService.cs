using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Rendering-library-agnostic PDF link extraction. Returns clickable link
/// regions in page-point space (origin top-left, Y-down).
/// </summary>
public interface IPdfLinkService
{
    List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex);
}
