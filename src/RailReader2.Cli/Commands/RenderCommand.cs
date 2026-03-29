using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader.Cli.Commands;

public static class RenderCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasFlag(args, "help") || Program.HasFlag(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        var pdfPath = Program.GetRequiredPdf(args);
        var pageRange = Program.GetOption(args, "pages");
        var dpiStr = Program.GetOption(args, "dpi");
        var effectName = Program.GetOption(args, "effect");
        var intensityStr = Program.GetOption(args, "intensity");
        var withAnnotations = Program.HasFlag(args, "annotations");
        var outputDir = Program.GetOption(args, "output-dir") ?? "screenshots";

        var dpi = dpiStr != null && int.TryParse(dpiStr, out var d) ? d : 300;
        var intensity = intensityStr != null && float.TryParse(intensityStr, out var f) ? f : 1.0f;
        var effect = ParseEffect(effectName);

        var pdf = factory.CreatePdfService(pdfPath);
        var (pages, rangeError) = PageRangeParser.Parse(pageRange, pdf.PageCount);
        if (rangeError != null)
            return Program.Fail(rangeError);

        Directory.CreateDirectory(outputDir);

        AnnotationFile? annotations = null;
        if (withAnnotations)
            annotations = AnnotationService.Load(pdfPath);

        using var colourEffects = new ColourEffectShaders(logger);
        var paint = colourEffects.CreatePaint(effect, intensity);

        int rendered = 0;
        foreach (var pageIdx in pages!)
        {
            using var renderedPage = pdf.RenderPage(pageIdx, dpi);
            var srcBitmap = ((SkiaRenderedPage)renderedPage).Bitmap;

            var pageAnnotations = withAnnotations && annotations != null
                && annotations.Pages.TryGetValue(pageIdx, out var pa) && pa.Count > 0 ? pa : null;

            bool needsCompositing = paint != null || pageAnnotations != null;

            if (!needsCompositing)
            {
                var outputPath = Path.Combine(outputDir, $"page_{pageIdx + 1:D3}.png");
                ScreenshotCompositor.SavePng(srcBitmap, outputPath);
                rendered++;
                Console.Error.WriteLine($"  Rendered page {pageIdx + 1}/{pdf.PageCount} -> {outputPath}");
                continue;
            }

            using var surface = SKSurface.Create(new SKImageInfo(srcBitmap.Width, srcBitmap.Height));
            var canvas = surface.Canvas;

            if (paint != null)
            {
                canvas.SaveLayer(paint);
                canvas.DrawBitmap(srcBitmap, 0, 0);
                canvas.Restore();
            }
            else
            {
                canvas.DrawBitmap(srcBitmap, 0, 0);
            }

            if (pageAnnotations != null)
            {
                var (pw, ph) = pdf.GetPageSize(pageIdx);
                float scaleX = srcBitmap.Width / (float)pw;
                float scaleY = srcBitmap.Height / (float)ph;

                canvas.Save();
                canvas.Scale(scaleX, scaleY);

                var expandedNotes = new List<TextNoteAnnotation>();
                foreach (var ann in pageAnnotations)
                {
                    if (ann is TextNoteAnnotation tn)
                    {
                        tn.IsExpanded = true;
                        expandedNotes.Add(tn);
                    }
                }

                AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, null);

                foreach (var tn in expandedNotes)
                    tn.IsExpanded = false;

                canvas.Restore();
            }

            var outPath = Path.Combine(outputDir, $"page_{pageIdx + 1:D3}.png");
            using var image = surface.Snapshot();
            using var outBitmap = SKBitmap.FromImage(image);
            ScreenshotCompositor.SavePng(outBitmap, outPath);

            rendered++;
            Console.Error.WriteLine($"  Rendered page {pageIdx + 1}/{pdf.PageCount} -> {outPath}");
        }

        Console.Error.WriteLine($"Done: {rendered} page(s) rendered to {Path.GetFullPath(outputDir)}");
        return 0;
    }

    static ColourEffect ParseEffect(string? name) => name?.ToLowerInvariant() switch
    {
        "highcontrast" or "high_contrast" => ColourEffect.HighContrast,
        "highvisibility" or "high_visibility" => ColourEffect.HighVisibility,
        "amber" => ColourEffect.Amber,
        "invert" => ColourEffect.Invert,
        null or "none" => ColourEffect.None,
        _ => ColourEffect.None
    };

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli render — Render PDF pages as PNG images");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli render <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --pages <range>       Page range, e.g. \"1,3,5-10\" (default: all)");
        Console.WriteLine("  --dpi <int>           Render DPI (default: 300)");
        Console.WriteLine("  --effect <name>       Colour effect: none, highcontrast, highvisibility, amber, invert");
        Console.WriteLine("  --intensity <float>   Effect intensity 0.0-1.0 (default: 1.0)");
        Console.WriteLine("  --annotations         Burn annotations into rendered pages");
        Console.WriteLine("  --output-dir <path>   Output directory (default: ./screenshots)");
    }
}
