using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Rendering-library-agnostic PDF bookmark/outline extraction.
/// </summary>
public interface IPdfOutlineService
{
    List<OutlineEntry> Extract(byte[] pdfBytes);
}
