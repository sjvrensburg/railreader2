using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader.Core.Services;

/// <summary>
/// Exports a PDF with annotations rasterised onto each page.
/// Uses SKDocument.CreatePdf() to produce a multi-page raster PDF.
/// Will move to RailReader.Renderer.Skia in a future step.
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
        using var stream = File.Create(outputPath);
        using var document = SKDocument.CreatePdf(stream);

        var collapsed = new List<TextNoteAnnotation>();

        for (int page = 0; page < pdf.PageCount; page++)
        {
            onProgress?.Invoke(page, pdf.PageCount);

            var (pageW, pageH) = pdf.GetPageSize(page);

            // Render page bitmap at export DPI
            using var rendered = (SkiaRenderedPage)pdf.RenderPage(page, dpi);
            var bitmap = rendered.Bitmap;
            float scaleX = (float)bitmap.Width / (float)pageW;
            float scaleY = (float)bitmap.Height / (float)pageH;

            // Create PDF page at bitmap dimensions (pixels)
            using var canvas = document.BeginPage(bitmap.Width, bitmap.Height);

            // Draw the PDF page bitmap
            canvas.DrawBitmap(bitmap, 0, 0);

            // Draw annotations scaled to match the DPI
            if (annotations.Pages.TryGetValue(page, out var pageAnnotations) && pageAnnotations.Count > 0)
            {
                // Expand all text notes so their popup text is rendered into the export
                collapsed.Clear();
                foreach (var ann in pageAnnotations)
                {
                    if (ann is TextNoteAnnotation tn && !tn.IsExpanded)
                    {
                        tn.IsExpanded = true;
                        collapsed.Add(tn);
                    }
                }

                try
                {
                    canvas.Save();
                    canvas.Scale(scaleX, scaleY);
                    AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, null);
                    canvas.Restore();
                }
                finally
                {
                    foreach (var tn in collapsed)
                        tn.IsExpanded = false;
                }
            }

            document.EndPage();
        }

        document.Close();
    }
}
