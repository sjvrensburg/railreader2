using System.Runtime.InteropServices;
using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Exports a PDF with annotations as native PDF annotation objects.
/// Copies original pages verbatim (preserving vector content) and overlays
/// highlights, ink strokes, rectangles, and text notes as proper PDF annotations.
/// </summary>
public static class AnnotationExportService
{
    public static void Export(
        IPdfService pdf,
        AnnotationFile annotations,
        string outputPath,
        int dpi = 300,
        Action<int, int>? onProgress = null)
    {
        var pdfBytes = pdf.PdfBytes;
        var pinnedSrc = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        try
        {
            var srcDoc = FPDF_LoadMemDocument(pinnedSrc.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (srcDoc == IntPtr.Zero)
                throw new InvalidOperationException("Failed to load source PDF via PDFium");

            var destDoc = FPDF_CreateNewDocument();
            if (destDoc == IntPtr.Zero)
            {
                FPDF_CloseDocument(srcDoc);
                throw new InvalidOperationException("Failed to create new PDF document");
            }

            try
            {
                // Copy all pages from source
                if (!FPDF_ImportPages(destDoc, srcDoc, null, 0))
                    throw new InvalidOperationException("Failed to import pages from source PDF");

                // Add annotations to pages that have them
                for (int pageIdx = 0; pageIdx < pdf.PageCount; pageIdx++)
                {
                    onProgress?.Invoke(pageIdx, pdf.PageCount);

                    if (!annotations.Pages.TryGetValue(pageIdx, out var pageAnns) || pageAnns.Count == 0)
                        continue;

                    var page = FPDF_LoadPage(destDoc, pageIdx);
                    if (page == IntPtr.Zero) continue;

                    try
                    {
                        var (cropLeft, cropBottom, visibleHeight) = GetCropBoxTransform(page);

                        foreach (var ann in pageAnns)
                            WriteAnnotation(page, ann, cropLeft, cropBottom, visibleHeight);
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }
                }

                // Save to output file
                SaveDocument(destDoc, outputPath);
            }
            finally
            {
                FPDF_CloseDocument(destDoc);
                FPDF_CloseDocument(srcDoc);
            }
        }
        finally
        {
            pinnedSrc.Free();
        }
    }

    private static void WriteAnnotation(IntPtr page, Annotation ann,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        switch (ann)
        {
            case HighlightAnnotation h:
                WriteHighlight(page, h, cropLeft, cropBottom, visibleHeight);
                break;
            case FreehandAnnotation f:
                WriteInk(page, f, cropLeft, cropBottom, visibleHeight);
                break;
            case RectAnnotation r:
                WriteRect(page, r, cropLeft, cropBottom, visibleHeight);
                break;
            case TextNoteAnnotation tn:
                WriteTextNote(page, tn, cropLeft, cropBottom, visibleHeight);
                break;
        }
    }

    private static void WriteHighlight(IntPtr page, HighlightAnnotation h,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (h.Rects.Count == 0) return;

        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_HIGHLIGHT);
        if (annot == IntPtr.Zero) return;

        try
        {
            ParseColor(h.Color, h.Opacity, out uint r, out uint g, out uint b, out uint a);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, r, g, b, a);
            FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var rect in h.Rects)
            {
                var (lx, by) = PagePointToPdf(rect.X, rect.Y + rect.H, cropLeft, cropBottom, visibleHeight);
                var (rx, ty) = PagePointToPdf(rect.X + rect.W, rect.Y, cropLeft, cropBottom, visibleHeight);

                // Quad points: lower-left, lower-right, upper-left, upper-right
                var quad = new FsQuadPointsF
                {
                    X1 = lx, Y1 = by,   // lower-left
                    X2 = rx, Y2 = by,   // lower-right
                    X3 = lx, Y3 = ty,   // upper-left
                    X4 = rx, Y4 = ty,   // upper-right
                };
                FPDFAnnot_AppendAttachmentPoints(annot, ref quad);

                minX = Math.Min(minX, lx);
                minY = Math.Min(minY, by);
                maxX = Math.Max(maxX, rx);
                maxY = Math.Max(maxY, ty);
            }

            var boundingRect = new FsRectF { Left = minX, Bottom = minY, Right = maxX, Top = maxY };
            FPDFAnnot_SetRect(annot, ref boundingRect);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void WriteInk(IntPtr page, FreehandAnnotation f,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (f.Points.Count < 2) return;

        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_INK);
        if (annot == IntPtr.Zero) return;

        try
        {
            ParseColor(f.Color, f.Opacity, out uint r, out uint g, out uint b, out uint a);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, r, g, b, a);
            FPDFAnnot_SetBorder(annot, 0, 0, f.StrokeWidth);
            FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);

            var pdfPoints = new FsPointF[f.Points.Count];
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < f.Points.Count; i++)
            {
                var (px, py) = PagePointToPdf(f.Points[i].X, f.Points[i].Y, cropLeft, cropBottom, visibleHeight);
                pdfPoints[i] = new FsPointF { X = px, Y = py };
                minX = Math.Min(minX, px);
                minY = Math.Min(minY, py);
                maxX = Math.Max(maxX, px);
                maxY = Math.Max(maxY, py);
            }

            FPDFAnnot_AddInkStroke(annot, pdfPoints, (nuint)pdfPoints.Length);

            // Pad bounding rect by stroke width
            float pad = f.StrokeWidth / 2f;
            var boundingRect = new FsRectF
            {
                Left = minX - pad, Bottom = minY - pad,
                Right = maxX + pad, Top = maxY + pad,
            };
            FPDFAnnot_SetRect(annot, ref boundingRect);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void WriteRect(IntPtr page, RectAnnotation ra,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_SQUARE);
        if (annot == IntPtr.Zero) return;

        try
        {
            ParseColor(ra.Color, ra.Opacity, out uint r, out uint g, out uint b, out uint a);

            if (ra.Filled)
            {
                FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_INTERIOR, r, g, b, a);
                // Transparent border for filled rects
                FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, r, g, b, a);
            }
            else
            {
                FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, r, g, b, 255);
            }

            FPDFAnnot_SetBorder(annot, 0, 0, ra.StrokeWidth);
            FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);

            var (lx, by) = PagePointToPdf(ra.X, ra.Y + ra.H, cropLeft, cropBottom, visibleHeight);
            var (rx, ty) = PagePointToPdf(ra.X + ra.W, ra.Y, cropLeft, cropBottom, visibleHeight);

            var rect = new FsRectF { Left = lx, Bottom = by, Right = rx, Top = ty };
            FPDFAnnot_SetRect(annot, ref rect);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void WriteTextNote(IntPtr page, TextNoteAnnotation tn,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_TEXT);
        if (annot == IntPtr.Zero) return;

        try
        {
            ParseColor(tn.Color, tn.Opacity, out uint r, out uint g, out uint b, out uint a);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, r, g, b, a);
            FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);

            var (px, py) = PagePointToPdf(tn.X, tn.Y, cropLeft, cropBottom, visibleHeight);

            // Standard sticky note icon size: 24×24 pt
            var rect = new FsRectF { Left = px, Bottom = py - 24, Right = px + 24, Top = py };
            FPDFAnnot_SetRect(annot, ref rect);

            // Set the note text as Contents
            if (!string.IsNullOrEmpty(tn.Text))
            {
                var utf16Bytes = Encoding.Unicode.GetBytes(tn.Text + "\0");
                var pinnedText = GCHandle.Alloc(utf16Bytes, GCHandleType.Pinned);
                try
                {
                    FPDFAnnot_SetStringValue(annot, "Contents", pinnedText.AddrOfPinnedObject());
                }
                finally
                {
                    pinnedText.Free();
                }
            }
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void ParseColor(string hexColor, float opacity,
        out uint r, out uint g, out uint b, out uint a)
    {
        // Parse "#RRGGBB" or "#AARRGGBB"
        r = g = b = 0;
        a = (uint)(opacity * 255);

        if (string.IsNullOrEmpty(hexColor) || hexColor[0] != '#')
            return;

        var hex = hexColor.AsSpan(1);
        if (hex.Length == 6)
        {
            r = uint.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            g = uint.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            b = uint.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
        }
        else if (hex.Length == 8)
        {
            uint hexA = uint.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            r = uint.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            g = uint.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            b = uint.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
            a = (uint)(hexA * opacity / 255);
        }
    }

    private static void SaveDocument(IntPtr document, string outputPath)
    {
        using var stream = File.Create(outputPath);

        WriteBlockDelegate writeBlock = (IntPtr self, IntPtr data, uint size) =>
        {
            var buffer = new byte[size];
            Marshal.Copy(data, buffer, 0, (int)size);
            stream.Write(buffer, 0, (int)size);
            return 1; // success
        };

        // Pin the delegate to prevent GC during the native call
        var writeBlockPtr = Marshal.GetFunctionPointerForDelegate(writeBlock);
        var fileWrite = new FpdfFileWrite
        {
            Version = 1,
            WriteBlock = writeBlockPtr,
        };

        if (!FPDF_SaveAsCopy(document, ref fileWrite, 0))
            throw new InvalidOperationException("Failed to save PDF document");

        GC.KeepAlive(writeBlock);
    }
}
