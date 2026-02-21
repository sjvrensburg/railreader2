using System.Runtime.InteropServices;
using RailReader2.Models;

namespace RailReader2.Services;

/// <summary>
/// Extracts per-page text and character bounding boxes from PDFs via PDFium P/Invoke.
/// Relies on PDFtoImage having already loaded the native pdfium library.
/// </summary>
public static class PdfTextService
{
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
                return new PageText("", []);

            page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero)
                return new PageText("", []);

            double pageHeight = FPDF_GetPageHeight(page);

            textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero)
                return new PageText("", []);

            int charCount = FPDFText_CountChars(textPage);
            if (charCount <= 0)
                return new PageText("", []);

            // Extract full text (UTF-16LE, two-pass pattern)
            // Buffer size is in bytes, including null terminator (2 bytes per char + 2)
            int bufferLen = (charCount + 1) * 2;
            var textBuffer = Marshal.AllocHGlobal(bufferLen);
            string text;
            try
            {
                int written = FPDFText_GetText(textPage, 0, charCount, textBuffer);
                text = Marshal.PtrToStringUni(textBuffer, Math.Max(0, written - 1)) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(textBuffer);
            }

            // Extract per-character bounding boxes
            var charBoxes = new List<CharBox>(charCount);
            for (int i = 0; i < charCount; i++)
            {
                double left = 0, right = 0, bottom = 0, top = 0;
                if (FPDFText_GetCharBox(textPage, i, ref left, ref right, ref bottom, ref top))
                {
                    // Convert from PDF user space (bottom-left origin, Y-up)
                    // to page-point space (top-left origin, Y-down)
                    float tlY = (float)(pageHeight - top);
                    float brY = (float)(pageHeight - bottom);
                    charBoxes.Add(new CharBox(i, (float)left, tlY, (float)right, brY));
                }
                else
                {
                    // Whitespace or control characters may not have a box
                    charBoxes.Add(new CharBox(i, 0, 0, 0, 0));
                }
            }

            return new PageText(text, charBoxes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PdfText] Failed to extract text for page {pageIndex}: {ex.Message}");
            return new PageText("", []);
        }
        finally
        {
            if (textPage != IntPtr.Zero) FPDFText_ClosePage(textPage);
            if (page != IntPtr.Zero) FPDF_ClosePage(page);
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            if (pinned.IsAllocated) pinned.Free();
        }
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
    [DllImport(Lib)] private static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, IntPtr result);
    [DllImport(Lib)] private static extern bool FPDFText_GetCharBox(IntPtr textPage, int index,
        ref double left, ref double right, ref double bottom, ref double top);
}
