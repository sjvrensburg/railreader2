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

                // Convert from PDF user space (Y-up, MediaBox) to page-point space (Y-down, CropBox)
                float left = fsRect.Left - offsetX;
                float right = fsRect.Right - offsetX;
                float top = (float)(visibleHeight - (fsRect.Top - offsetY));
                float bottom = (float)(visibleHeight - (fsRect.Bottom - offsetY));
                var rect = new RectF(left, top, right, bottom);

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

    public static PdfLink? HitTestLink(byte[] pdfBytes, int pageIndex, double pageX, double pageY)
    {
        IntPtr doc = IntPtr.Zero;
        IntPtr page = IntPtr.Zero;
        GCHandle pinned = default;

        try
        {
            pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (doc == IntPtr.Zero) return null;

            page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero) return null;

            var (offsetX, offsetY, visibleHeight) = GetCropBoxTransform(page);

            // Convert page-point space (Y-down) back to PDF user space (Y-up) for PDFium
            double pdfX = pageX + offsetX;
            double pdfY = visibleHeight - pageY + offsetY;

            IntPtr link = FPDFLink_GetLinkAtPoint(page, pdfX, pdfY);
            if (link == IntPtr.Zero) return null;

            if (!FPDFLink_GetAnnotRect(link, out FsRectF fsRect))
                return null;

            float left = fsRect.Left - offsetX;
            float right = fsRect.Right - offsetX;
            float top = (float)(visibleHeight - (fsRect.Top - offsetY));
            float bottom = (float)(visibleHeight - (fsRect.Bottom - offsetY));

            var dest = ResolveDestination(doc, link);
            if (dest is null) return null;

            return new PdfLink { Rect = new RectF(left, top, right, bottom), Destination = dest };
        }
        catch (Exception ex)
        {
            Logger.Error($"[PdfLink] Failed to hit-test link on page {pageIndex}", ex);
            return null;
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
