using System.Runtime.InteropServices;
using System.Text;
using RailReader2.Models;

namespace RailReader2.Services;

/// <summary>
/// Extracts PDF bookmarks/outline using PDFium's native API.
/// Relies on PDFtoImage having already loaded the native pdfium library.
/// </summary>
public static class PdfOutlineExtractor
{
    public static List<OutlineEntry> Extract(byte[] pdfBytes)
    {
        var result = new List<OutlineEntry>();

        IntPtr doc = IntPtr.Zero;
        GCHandle pinned = default;
        try
        {
            pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (doc == IntPtr.Zero)
                return result;

            var root = FPDFBookmark_GetFirstChild(doc, IntPtr.Zero);
            ReadBookmarks(doc, root, result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Outline] Failed to extract: {ex.Message}");
        }
        finally
        {
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            if (pinned.IsAllocated) pinned.Free();
        }

        return result;
    }

    private static void ReadBookmarks(IntPtr doc, IntPtr bookmark, List<OutlineEntry> entries)
    {
        while (bookmark != IntPtr.Zero)
        {
            var entry = new OutlineEntry
            {
                Title = GetBookmarkTitle(bookmark),
                Page = GetBookmarkPage(doc, bookmark),
            };

            var child = FPDFBookmark_GetFirstChild(doc, bookmark);
            if (child != IntPtr.Zero)
                ReadBookmarks(doc, child, entry.Children);

            entries.Add(entry);
            bookmark = FPDFBookmark_GetNextSibling(doc, bookmark);
        }
    }

    private static string GetBookmarkTitle(IntPtr bookmark)
    {
        // First call gets required buffer size (in bytes, including null terminator)
        int len = (int)FPDFBookmark_GetTitle(bookmark, IntPtr.Zero, 0);
        if (len <= 0) return "";

        var buffer = Marshal.AllocHGlobal(len);
        try
        {
            FPDFBookmark_GetTitle(bookmark, buffer, (uint)len);
            // PDFium returns UTF-16LE
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? GetBookmarkPage(IntPtr doc, IntPtr bookmark)
    {
        var dest = FPDFBookmark_GetDest(doc, bookmark);
        if (dest == IntPtr.Zero)
        {
            var action = FPDFBookmark_GetAction(bookmark);
            if (action != IntPtr.Zero)
                dest = FPDFAction_GetDest(doc, action);
        }
        if (dest == IntPtr.Zero) return null;

        int page = FPDFDest_GetDestPageIndex(doc, dest);
        return page >= 0 ? page : null;
    }

    // PDFium P/Invoke declarations
    private const string Lib = "pdfium";

    [DllImport(Lib)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] private static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] private static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] private static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, uint buflen);
    [DllImport(Lib)] private static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] private static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Lib)] private static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [DllImport(Lib)] private static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
}
