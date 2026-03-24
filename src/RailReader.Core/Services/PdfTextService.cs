using System.Runtime.InteropServices;
using RailReader.Core;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Extracts per-page text and character bounding boxes from PDFs via PDFium P/Invoke.
/// Relies on PDFtoImage having already loaded the native pdfium library.
/// </summary>
public static class PdfTextService
{
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    private static readonly PageText s_empty = new("", []);

    /// <summary>
    /// Extracts all text and per-character bounding boxes for a given page.
    /// PDFium returns coordinates in PDF user space (origin bottom-left, Y-up).
    /// This method converts them to page-point space (origin top-left, Y-down)
    /// matching BBox and the overlay layers.
    /// </summary>
    public static PageText ExtractPageText(byte[] pdfBytes, int pageIndex)
    {
        return WithTextPage(pdfBytes, pageIndex, s_empty, "extract text",
            (textPage, offsetX, offsetY, visibleHeight) =>
            {
                int charCount = FPDFText_CountChars(textPage);
                if (charCount <= 0)
                    return s_empty;

                // Build text character-by-character using FPDFText_GetUnicode to ensure
                // 1:1 index correspondence with FPDFText_GetCharBox.
                var textChars = new char[charCount];
                var charBoxes = new List<CharBox>(charCount);
                for (int i = 0; i < charCount; i++)
                {
                    uint unicode = FPDFText_GetUnicode(textPage, i);
                    textChars[i] = unicode <= 0xFFFF ? (char)unicode : '\uFFFD';

                    double left = 0, right = 0, bottom = 0, top = 0;
                    if (FPDFText_GetCharBox(textPage, i, ref left, ref right, ref bottom, ref top))
                    {
                        // Shift from MediaBox to CropBox space, then convert
                        // from PDF user space (Y-up) to page-point space (Y-down)
                        float adjLeft = (float)(left - offsetX);
                        float adjRight = (float)(right - offsetX);
                        float tlY = (float)(visibleHeight - (top - offsetY));
                        float brY = (float)(visibleHeight - (bottom - offsetY));
                        charBoxes.Add(new CharBox(i, adjLeft, tlY, adjRight, brY));
                    }
                    else
                    {
                        charBoxes.Add(new CharBox(i, 0, 0, 0, 0));
                    }
                }
                string text = new string(textChars);

                return new PageText(text, charBoxes);
            });
    }

    /// <summary>
    /// Uses PDFium's FPDFText_CountRects/GetRect to get visual bounding rectangles
    /// for character ranges on a page. Returns rects in page-point space (origin top-left, Y-down),
    /// adjusted for CropBox offset so highlights align with the rendered page.
    /// </summary>
    public static List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges)
    {
        var result = new List<List<RectF>>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
            result.Add([]);

        return WithTextPage(pdfBytes, pageIndex, result, "get text range rects",
            (textPage, offsetX, offsetY, visibleHeight) =>
            {
                for (int i = 0; i < ranges.Count; i++)
                {
                    var (charStart, charLength) = ranges[i];
                    int rectCount = FPDFText_CountRects(textPage, charStart, charLength);
                    for (int r = 0; r < rectCount; r++)
                    {
                        double left = 0, top = 0, right = 0, bottom = 0;
                        if (FPDFText_GetRect(textPage, r, ref left, ref top, ref right, ref bottom))
                        {
                            // Shift from MediaBox space to CropBox space, then
                            // convert from PDF user space (Y-up) to page-point space (Y-down)
                            float adjLeft = (float)(left - offsetX);
                            float adjRight = (float)(right - offsetX);
                            float tlY = (float)(visibleHeight - (top - offsetY));
                            float brY = (float)(visibleHeight - (bottom - offsetY));
                            result[i].Add(new RectF(adjLeft, tlY, adjRight, brY));
                        }
                    }
                }

                return result;
            });
    }

    /// <summary>
    /// Loads a PDFium document and text page from in-memory PDF bytes, invokes
    /// <paramref name="action"/> with the text page handle and CropBox transform,
    /// then tears everything down in a finally block.
    /// Returns <paramref name="defaultValue"/> if the document, page, or text page
    /// fails to load, or if an exception is thrown.
    /// </summary>
    private static T WithTextPage<T>(byte[] pdfBytes, int pageIndex, T defaultValue,
        string operationName, Func<IntPtr, float, float, double, T> action)
    {
        IntPtr doc = IntPtr.Zero;
        IntPtr page = IntPtr.Zero;
        IntPtr textPage = IntPtr.Zero;
        GCHandle pinned = default;

        try
        {
            pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (doc == IntPtr.Zero)
                return defaultValue;

            page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero)
                return defaultValue;

            var (offsetX, offsetY, visibleHeight) = GetCropBoxTransform(page);

            textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero)
                return defaultValue;

            return action(textPage, offsetX, offsetY, visibleHeight);
        }
        catch (Exception ex)
        {
            Logger.Error($"[PdfText] Failed to {operationName} for page {pageIndex}", ex);
            return defaultValue;
        }
        finally
        {
            if (textPage != IntPtr.Zero) FPDFText_ClosePage(textPage);
            if (page != IntPtr.Zero) FPDF_ClosePage(page);
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    /// <summary>
    /// Computes the CropBox-to-page-point-space transform for a loaded page.
    /// PDFium text APIs return coordinates in MediaBox space; if the page has a
    /// CropBox offset from the MediaBox origin, we subtract it so coordinates
    /// align with the rendered (CropBox) area.
    /// </summary>
    private static (float OffsetX, float OffsetY, double VisibleHeight) GetCropBoxTransform(IntPtr page)
    {
        float cropLeft = 0, cropBottom = 0, cropRight = 0, cropTop = 0;
        bool hasCropBox = FPDFPage_GetCropBox(page, ref cropLeft, ref cropBottom, ref cropRight, ref cropTop);
        float offsetX = hasCropBox ? cropLeft : 0;
        float offsetY = hasCropBox ? cropBottom : 0;
        double visibleHeight = hasCropBox ? cropTop - cropBottom : FPDF_GetPageHeight(page);
        return (offsetX, offsetY, visibleHeight);
    }
}
