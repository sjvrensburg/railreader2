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
