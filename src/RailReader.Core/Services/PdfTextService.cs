using System.Runtime.InteropServices;
using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

/// <summary>
/// Extracts per-page text and character bounding boxes from PDFs via PDFium P/Invoke.
/// Relies on PDFtoImage having already loaded the native pdfium library.
/// </summary>
public static class PdfTextService
{
    private static readonly PageText s_empty = new("", []);

    /// <summary>
    /// Extracts all text and per-character bounding boxes for a given page.
    /// PDFium returns coordinates in PDF user space (origin bottom-left, Y-up).
    /// This method converts them to page-point space (origin top-left, Y-down)
    /// matching BBox and the overlay layers.
    /// </summary>
    public static PageText ExtractPageText(byte[] pdfBytes, int pageIndex)
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
                return s_empty;

            page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero)
                return s_empty;

            var (offsetX, offsetY, visibleHeight) = GetCropBoxTransform(page);

            textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero)
                return s_empty;

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
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PdfText] Failed to extract text for page {pageIndex}: {ex.Message}");
            return s_empty;
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
    /// Uses PDFium's FPDFText_CountRects/GetRect to get visual bounding rectangles
    /// for character ranges on a page. Returns rects in page-point space (origin top-left, Y-down),
    /// adjusted for CropBox offset so highlights align with the rendered page.
    /// </summary>
    public static List<List<SKRect>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges)
    {
        var result = new List<List<SKRect>>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
            result.Add([]);

        IntPtr doc = IntPtr.Zero;
        IntPtr page = IntPtr.Zero;
        IntPtr textPage = IntPtr.Zero;
        GCHandle pinned = default;

        try
        {
            pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (doc == IntPtr.Zero) return result;

            page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero) return result;

            var (offsetX, offsetY, visibleHeight) = GetCropBoxTransform(page);

            textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero) return result;

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
                        result[i].Add(new SKRect(adjLeft, tlY, adjRight, brY));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PdfText] Failed to get text range rects for page {pageIndex}: {ex.Message}");
        }
        finally
        {
            if (textPage != IntPtr.Zero) FPDFText_ClosePage(textPage);
            if (page != IntPtr.Zero) FPDF_ClosePage(page);
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            if (pinned.IsAllocated) pinned.Free();
        }

        return result;
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

    // PDFium P/Invoke declarations
    private const string Lib = "pdfium";

    [DllImport(Lib)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] private static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Lib)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] private static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] private static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] private static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] private static extern bool FPDFText_GetCharBox(IntPtr textPage, int index,
        ref double left, ref double right, ref double bottom, ref double top);
    [DllImport(Lib)] private static extern bool FPDFPage_GetCropBox(IntPtr page,
        ref float left, ref float bottom, ref float right, ref float top);
    [DllImport(Lib)] private static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
    [DllImport(Lib)] private static extern bool FPDFText_GetRect(IntPtr textPage, int rectIndex,
        ref double left, ref double top, ref double right, ref double bottom);
}
