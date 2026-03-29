using System.Runtime.InteropServices;

namespace RailReader.Core.Services;

/// <summary>
/// Centralised PDFium P/Invoke declarations used by PdfOutlineExtractor and PdfTextService.
/// </summary>
internal static class PdfiumNative
{
    private const string Lib = "pdfium";

    // Document
    [DllImport(Lib)] internal static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] internal static extern void FPDF_CloseDocument(IntPtr document);

    // Pages
    [DllImport(Lib)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] internal static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] internal static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Lib)] internal static extern bool FPDFPage_GetCropBox(IntPtr page,
        ref float left, ref float bottom, ref float right, ref float top);

    // Bookmarks
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, uint buflen);
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] internal static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Lib)] internal static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [DllImport(Lib)] internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    // Links
    [DllImport(Lib)] internal static extern bool FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr linkAnnot);
    [DllImport(Lib)] internal static extern bool FPDFLink_GetAnnotRect(IntPtr linkAnnot, out FsRectF rect);
    [DllImport(Lib)] internal static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
    [DllImport(Lib)] internal static extern IntPtr FPDFLink_GetAction(IntPtr link);
    [DllImport(Lib)] internal static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Lib)] internal static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, IntPtr buffer, uint buflen);
    [DllImport(Lib)] internal static extern IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);

    // PDFium action types
    internal const uint PDFACTION_GOTO = 1;      // Internal "go to destination"
    internal const uint PDFACTION_REMOTEGOTO = 2; // Remote "go to destination" (another PDF)
    internal const uint PDFACTION_URI = 3;        // Open a URI
    internal const uint PDFACTION_LAUNCH = 4;     // Launch an application

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsRectF
    {
        public float Left;
        public float Bottom;
        public float Right;
        public float Top;
    }

    /// <summary>
    /// Computes the CropBox-to-page-point-space transform for a loaded page.
    /// PDFium APIs return coordinates in MediaBox space; if the page has a
    /// CropBox offset from the MediaBox origin, we subtract it so coordinates
    /// align with the rendered (CropBox) area.
    /// </summary>
    internal static (float OffsetX, float OffsetY, double VisibleHeight) GetCropBoxTransform(IntPtr page)
    {
        float cropLeft = 0, cropBottom = 0, cropRight = 0, cropTop = 0;
        bool hasCropBox = FPDFPage_GetCropBox(page, ref cropLeft, ref cropBottom, ref cropRight, ref cropTop);
        float offsetX = hasCropBox ? cropLeft : 0;
        float offsetY = hasCropBox ? cropBottom : 0;
        double visibleHeight = hasCropBox ? cropTop - cropBottom : FPDF_GetPageHeight(page);
        return (offsetX, offsetY, visibleHeight);
    }

    // Text
    [DllImport(Lib)] internal static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] internal static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] internal static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] internal static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] internal static extern bool FPDFText_GetCharBox(IntPtr textPage, int index,
        ref double left, ref double right, ref double bottom, ref double top);
    [DllImport(Lib)] internal static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
    [DllImport(Lib)] internal static extern bool FPDFText_GetRect(IntPtr textPage, int rectIndex,
        ref double left, ref double top, ref double right, ref double bottom);
}
