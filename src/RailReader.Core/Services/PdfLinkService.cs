using System.Runtime.InteropServices;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Extracts clickable link regions from PDF pages via PDFium P/Invoke.
/// Returns links in page-point space (origin top-left, Y-down).
/// </summary>
internal static class PdfLinkService
{
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    private static readonly List<PdfLink> s_empty = [];

    public static List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex)
    {
        IntPtr doc = IntPtr.Zero;
        IntPtr page = IntPtr.Zero;
        GCHandle pinned = default;

        try
        {
            pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (doc == IntPtr.Zero) return s_empty;

            page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero) return s_empty;

            var (offsetX, offsetY, visibleHeight) = GetCropBoxTransform(page);
            var links = new List<PdfLink>();

            int startPos = 0;
            while (FPDFLink_Enumerate(page, ref startPos, out IntPtr linkAnnot))
            {
                if (!FPDFLink_GetAnnotRect(linkAnnot, out FsRectF fsRect))
                    continue;

                var rect = ToPageRect(fsRect, offsetX, offsetY, visibleHeight);

                var dest = ResolveDestination(doc, linkAnnot);
                if (dest is null) continue;

                links.Add(new PdfLink { Rect = rect, Destination = dest });
            }

            return links;
        }
        catch (Exception ex)
        {
            Logger.Error($"[PdfLink] Failed to extract links for page {pageIndex}", ex);
            return s_empty;
        }
        finally
        {
            if (page != IntPtr.Zero) FPDF_ClosePage(page);
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    private static PdfLinkDestination? ResolveDestination(IntPtr doc, IntPtr link)
    {
        // Try direct destination first (most internal links)
        IntPtr dest = FPDFLink_GetDest(doc, link);
        if (dest != IntPtr.Zero)
        {
            int pageIdx = FPDFDest_GetDestPageIndex(doc, dest);
            if (pageIdx >= 0)
                return new PageDestination { PageIndex = pageIdx };
        }

        // Fall back to action
        IntPtr action = FPDFLink_GetAction(link);
        if (action == IntPtr.Zero) return null;

        uint actionType = FPDFAction_GetType(action);
        switch (actionType)
        {
            case PDFACTION_GOTO:
                dest = FPDFAction_GetDest(doc, action);
                if (dest != IntPtr.Zero)
                {
                    int pageIdx = FPDFDest_GetDestPageIndex(doc, dest);
                    if (pageIdx >= 0)
                        return new PageDestination { PageIndex = pageIdx };
                }
                break;

            case PDFACTION_URI:
                uint len = FPDFAction_GetURIPath(doc, action, IntPtr.Zero, 0);
                if (len > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)len);
                    try
                    {
                        FPDFAction_GetURIPath(doc, action, buf, len);
                        string uri = Marshal.PtrToStringAnsi(buf, (int)len - 1) ?? "";
                        if (!string.IsNullOrWhiteSpace(uri))
                            return new UriDestination { Uri = uri };
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                break;
        }

        return null;
    }

    /// <summary>
    /// Converts an FsRectF from PDF user space (Y-up, MediaBox) to a normalized
    /// RectF in page-point space (Y-down, CropBox-adjusted).
    /// </summary>
    private static RectF ToPageRect(FsRectF fsRect, float offsetX, float offsetY, double visibleHeight)
    {
        float left = fsRect.Left - offsetX;
        float right = fsRect.Right - offsetX;
        float y1 = (float)(visibleHeight - (fsRect.Top - offsetY));
        float y2 = (float)(visibleHeight - (fsRect.Bottom - offsetY));
        return new RectF(left, y1, right, y2).Normalized();
    }
}
