using RailReader2.Models;
using RailReader2.Views;
using SkiaSharp;

namespace RailReader2.Services;

public static class AnnotationExportService
{
    /// <summary>
    /// Exports a PDF with annotations rasterised onto each page.
    /// Uses SKDocument.CreatePdf() to produce a multi-page raster PDF.
    /// </summary>
    public static void Export(
        PdfService pdf,
        AnnotationFile annotations,
        string outputPath,
        int dpi = 300,
        Action<int, int>? onProgress = null)
    {
        using var stream = File.Create(outputPath);
        using var document = SKDocument.CreatePdf(stream);

        for (int page = 0; page < pdf.PageCount; page++)
        {
            onProgress?.Invoke(page, pdf.PageCount);

            var (pageW, pageH) = pdf.GetPageSize(page);

            // Render page bitmap at export DPI
            using var bitmap = pdf.RenderPage(page, dpi);
            float scaleX = (float)bitmap.Width / (float)pageW;
            float scaleY = (float)bitmap.Height / (float)pageH;

            // Create PDF page at bitmap dimensions (pixels)
            using var canvas = document.BeginPage(bitmap.Width, bitmap.Height);

            // Draw the PDF page bitmap
            canvas.DrawBitmap(bitmap, 0, 0);

            // Draw annotations scaled to match the DPI
            if (annotations.Pages.TryGetValue(page, out var pageAnnotations) && pageAnnotations.Count > 0)
            {
                canvas.Save();
                canvas.Scale(scaleX, scaleY);
                AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, null);
                canvas.Restore();
            }

            document.EndPage();
        }

        document.Close();
    }
}
