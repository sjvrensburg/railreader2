using System.Runtime.CompilerServices;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Whether a page's text came from OCR rather than the PDF's own text layer. Core merges both into one
/// <c>PageText</c> with no marker, so this infers it: the page has recognised text but its native layer
/// is empty. OCR text is a machine reading of pixels, so previews label it unverified instead of
/// presenting it like text the author typed. UI-thread only (reads the page through PDFium).
/// </summary>
public static class OcrProvenance
{
    private static readonly ConditionalWeakTable<DocumentModel, Dictionary<int, bool>> s_native = [];

    /// <summary>True when <paramref name="page"/> currently has text and none of it is in the PDF's own text layer.</summary>
    public static bool IsOcrText(DocumentModel doc, int page)
    {
        if (doc.GetOrExtractText(page).CharBoxes.Count == 0) return false;   // nothing recognised (yet): not OCR text
        var cache = s_native.GetOrCreateValue(doc);
        if (!cache.TryGetValue(page, out bool hasNative))
        {
            try { hasNative = doc.PdfText.ExtractPageText(doc.Pdf.PdfBytes, page, doc.Pdf.Password).CharBoxes.Count > 0; }
            catch (Exception ex) { RailReaderLogging.Logger.Error($"OCR provenance check failed for page {page + 1}", ex); return false; }
            cache[page] = hasNative;
        }
        return !hasNative;
    }
}
