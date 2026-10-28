using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Adds an invisible OCR text layer to a copy of a PDF (railreader2#273, RailReaderCore#155): recognises
/// the pages without a native text layer and writes the result with Core's
/// <see cref="PdfTextLayerWriter"/>. Shared by the CLI's <c>ocr</c> command and the app's
/// "Add a Text Layer…" job so the two can't drift apart. Safe off the UI thread: every PDFium call goes
/// through Core's <c>PdfiumGate</c>. The <see cref="IOcrService"/> is not thread-safe, so a job needs
/// its own instance (never the analysis worker's).
/// </summary>
public static class OcrTextLayer
{
    /// <summary>The copy's default name: <c>&lt;name&gt;_ocr.pdf</c> beside the source.</summary>
    public static string DefaultOutputPath(string sourcePath)
        => Path.Combine(Path.GetDirectoryName(sourcePath) ?? "",
            Path.GetFileNameWithoutExtension(sourcePath) + "_ocr.pdf");

    /// <summary>
    /// Pages that need recognising: no native text, or only a layer this writer added earlier (a re-run
    /// replaces it). The writer skips native-text pages by itself, but recognising them first would be
    /// the bulk of the time on a mixed document.
    /// </summary>
    public static List<int> PagesNeedingOcr(IPdfService pdf, IPdfTextService text, CancellationToken ct = default)
    {
        var pages = new List<int>();
        for (int page = 0; page < pdf.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageText = text.ExtractPageText(pdf.PdfBytes, page, pdf.Password);
            if (pageText.CharBoxes.Count == 0 || pageText.FromOcr) pages.Add(page);
        }
        return pages;
    }

    /// <summary>
    /// Recognises <paramref name="pages"/> and writes the copy to <paramref name="outputPath"/>, which must
    /// not be <paramref name="sourcePath"/> (the file <paramref name="pdf"/> was opened from). Progress
    /// counts recognised pages, which is where the time goes; the write is quick. Writes nothing, and
    /// returns <see cref="TextLayerWriteResult.Empty"/>, when no page yields text.
    /// </summary>
    public static TextLayerWriteResult Run(IPdfService pdf, string sourcePath, IOcrService ocr,
        IReadOnlyList<int> pages, string outputPath, int dpi = OcrRecognizer.DefaultDpi,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(sourcePath), StringComparison.Ordinal))
            throw new InvalidOperationException("The text layer is written to a copy; choose an output other than the source PDF.");

        var recognised = new Dictionary<int, OcrPage>();
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, pages.Count));
            var page = OcrRecognizer.RecognizePage(pdf, ocr, pages[i], OcrMode.Full, dpi, ct);
            if (page.Lines.Count > 0) recognised[pages[i]] = page;
        }
        progress?.Report((pages.Count, pages.Count));
        if (recognised.Count == 0) return TextLayerWriteResult.Empty;

        ct.ThrowIfCancellationRequested();
        // Copy the file as it is on disk now, not the bytes the document was opened from: annotations
        // saved into the PDF since then are only on disk. Pages and geometry are the same either way.
        return PdfTextLayerWriter.Write(File.ReadAllBytes(sourcePath), recognised, outputPath,
            pdf.Password, ct: ct);
    }
}
