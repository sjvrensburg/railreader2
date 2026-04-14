using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader.Cli.Commands;

public static class AnnotationsCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasFlag(args, "help") || Program.HasFlag(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        var pdfPath = Program.GetRequiredPdf(args);
        var outputPath = Program.GetOption(args, "output");
        var format = Program.GetOption(args, "format") ?? "json";
        var pageRange = Program.GetOption(args, "pages");
        var includeText = Program.HasFlag(args, "include-text");
        var includeBlocks = Program.HasFlag(args, "include-blocks");

        if (format is not "json" and not "pdf")
            return Program.Fail($"Unknown format '{format}'. Valid options: json, pdf");

        var annotations = AnnotationService.Load(pdfPath);
        if (annotations == null)
        {
            Console.Error.WriteLine("No annotations found for this document.");
            return 0;
        }

        if (format == "pdf")
        {
            var outPath = outputPath ?? Path.ChangeExtension(Path.GetFileName(pdfPath), ".annotated.pdf");
            var pdf = factory.CreatePdfService(pdfPath);
            AnnotationExportService.Export(pdf, annotations, outPath);
            Console.Error.WriteLine($"Annotated PDF written to {Path.GetFullPath(outPath)}");
            return 0;
        }

        var pdf2 = factory.CreatePdfService(pdfPath);

        var (pages, rangeError) = PageRangeParser.Parse(pageRange, pdf2.PageCount);
        if (rangeError != null) return Program.Fail(rangeError);
        var pageSet = pages is not null ? new HashSet<int>(pages) : null;

        using var analyzer = Shared.CreateAnalyzer(includeBlocks);
        includeBlocks = analyzer is not null;

        IPdfTextService? textService = null;
        if (includeText)
            textService = factory.CreatePdfTextService();

        var result = new AnnotationExportOutput
        {
            Source = Path.GetFileName(pdfPath),
            ExportedAt = DateTime.UtcNow.ToString("O"),
            PageCount = pdf2.PageCount,
            Outline = pdf2.Outline.Select(Shared.SerializeOutlineEntry).ToList()
        };

        int failed = 0;
        foreach (var (pageIdx, pageAnnotations) in annotations.Pages.OrderBy(p => p.Key)
            .Where(p => pageSet is null || pageSet.Contains(p.Key)))
        {
            try
            {
                var (pw, ph) = pdf2.GetPageSize(pageIdx);
                var pageOutput = new AnnotationPageOutput
                {
                    Page = pageIdx,
                    Width = (float)pw,
                    Height = (float)ph
                };

                PageAnalysis? analysis = null;
                if (includeBlocks && analyzer != null)
                {
                    var (rgbBytes, pxW, pxH) = pdf2.RenderPagePixmap(pageIdx, 800);
                    analysis = analyzer.RunAnalysis(rgbBytes, pxW, pxH, pw, ph);
                }

                PageText? pageText = null;
                if (includeText && textService != null)
                    pageText = textService.ExtractPageText(pdf2.PdfBytes, pageIdx);

                foreach (var ann in pageAnnotations)
                {
                    var annOutput = SerializeAnnotation(ann);
                    var annBounds = AnnotationGeometry.GetAnnotationBounds(ann);

                    if (includeText && pageText != null && annBounds is { } textRect)
                        annOutput.Text = pageText.ExtractTextInRect(textRect.Left, textRect.Top, textRect.Right, textRect.Bottom);

                    if (includeBlocks && analysis != null && annBounds is { } blockRect)
                    {
                        foreach (var block in analysis.Blocks)
                        {
                            if (Overlaps(blockRect, block.BBox))
                            {
                                var blockOutput = new AnnotationBlockOutput
                                {
                                    Class = block.ClassId < LayoutConstants.LayoutClasses.Length
                                        ? LayoutConstants.LayoutClasses[block.ClassId]
                                        : $"class_{block.ClassId}",
                                    ClassId = block.ClassId,
                                    BBox = new BBoxOutput(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H),
                                    Confidence = block.Confidence
                                };

                                if (includeText && pageText != null)
                                    blockOutput.Text = pageText.ExtractBlockText(block);

                                annOutput.OverlappingBlocks.Add(blockOutput);
                            }
                        }
                    }

                    annOutput.NearestHeading = FindNearestHeading(annBounds, pageIdx, pdf2.Outline, analysis);
                    pageOutput.Annotations.Add(annOutput);
                }

                result.Pages.Add(pageOutput);
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  Error on page {pageIdx + 1}: {ex.Message}");
            }
        }

        result.Bookmarks = annotations.Bookmarks.Select(b => new BookmarkOutput
        {
            Name = b.Name,
            Page = b.Page
        }).ToList();

        var json = JsonSerializer.Serialize(result, CliJsonContext.Default.AnnotationExportOutput);

        if (outputPath != null)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);
            Console.Error.WriteLine($"Annotations written to {Path.GetFullPath(outputPath)}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return failed > 0 ? 1 : 0;
    }

    internal static bool Overlaps(RectF a, BBox b)
    {
        float bRight = b.X + b.W;
        float bBottom = b.Y + b.H;
        return a.Left < bRight && a.Right > b.X && a.Top < bBottom && a.Bottom > b.Y;
    }

    static AnnotationOutput SerializeAnnotation(Annotation ann)
    {
        if (ann is HighlightAnnotation h)
        {
            return new AnnotationOutput
            {
                Type = "highlight", Color = ann.Color, Opacity = ann.Opacity,
                Rects = h.Rects.Select(r => new RectOutput(r.X, r.Y, r.W, r.H)).ToList()
            };
        }
        if (ann is FreehandAnnotation f)
        {
            return new AnnotationOutput
            {
                Type = "freehand", Color = ann.Color, Opacity = ann.Opacity,
                StrokeWidth = f.StrokeWidth,
                Points = f.Points.Select(p => new PointOutput(p.X, p.Y)).ToList()
            };
        }
        if (ann is TextNoteAnnotation t)
        {
            return new AnnotationOutput
            {
                Type = "text_note", Color = ann.Color, Opacity = ann.Opacity,
                X = t.X, Y = t.Y, NoteText = t.Text
            };
        }
        if (ann is RectAnnotation r)
        {
            return new AnnotationOutput
            {
                Type = "rect", Color = ann.Color, Opacity = ann.Opacity,
                X = r.X, Y = r.Y, W = r.W, H = r.H,
                StrokeWidth = r.StrokeWidth, Filled = r.Filled
            };
        }
        return new AnnotationOutput { Type = "unknown", Color = ann.Color, Opacity = ann.Opacity };
    }

    static HeadingOutput? FindNearestHeading(RectF? annBounds, int pageIdx,
        List<OutlineEntry> outline, PageAnalysis? analysis)
    {
        if (annBounds is not { } annRect) return null;

        var bestOutline = FindNearestOutlineHeading(outline, pageIdx);
        if (bestOutline != null)
            return bestOutline;

        if (analysis == null) return null;

        LayoutBlock? bestBlock = null;
        float bestDist = float.MaxValue;
        foreach (var block in analysis.Blocks)
        {
            if (block.ClassId == LayoutConstants.ClassParagraphTitle
                || block.ClassId == LayoutConstants.ClassDocTitle)
            {
                float blockBottom = block.BBox.Y + block.BBox.H;
                if (blockBottom <= annRect.Top)
                {
                    float dist = annRect.Top - blockBottom;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestBlock = block;
                    }
                }
            }
        }

        if (bestBlock != null)
        {
            return new HeadingOutput
            {
                Title = LayoutConstants.LayoutClasses.Length > bestBlock.ClassId
                    ? LayoutConstants.LayoutClasses[bestBlock.ClassId] : $"class_{bestBlock.ClassId}",
                Source = "layout",
                Page = pageIdx
            };
        }

        return null;
    }

    static HeadingOutput? FindNearestOutlineHeading(List<OutlineEntry> outline, int pageIdx)
    {
        HeadingOutput? best = null;

        foreach (var entry in outline)
        {
            if (entry.Page.HasValue && entry.Page.Value <= pageIdx)
                best = new HeadingOutput { Title = entry.Title, Source = "outline", Page = entry.Page.Value };

            var childBest = FindNearestOutlineHeading(entry.Children, pageIdx);
            if (childBest != null)
                best = childBest;
        }

        return best;
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli annotations — Export annotations from a PDF");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli annotations <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output <path>       Output file path (default: stdout for JSON)");
        Console.WriteLine("  --format <json|pdf>   Export format (default: json)");
        Console.WriteLine("  --pages <range>       Page range to export (e.g. 1,3,5-10; default: all)");
        Console.WriteLine("  --include-text        Extract text under each annotation");
        Console.WriteLine("  --include-blocks      Correlate annotations with layout blocks (implies ONNX analysis)");
    }
}

public class AnnotationExportOutput
{
    public string Source { get; set; } = "";
    public string ExportedAt { get; set; } = "";
    public int PageCount { get; set; }
    public List<OutlineEntryOutput> Outline { get; set; } = [];
    public List<AnnotationPageOutput> Pages { get; set; } = [];
    public List<BookmarkOutput> Bookmarks { get; set; } = [];
}

public class AnnotationPageOutput
{
    public int Page { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public List<AnnotationOutput> Annotations { get; set; } = [];
}

public class AnnotationOutput
{
    public string Type { get; set; } = "";
    public string Color { get; set; } = "";
    public float Opacity { get; set; }
    public List<RectOutput>? Rects { get; set; }
    public float? StrokeWidth { get; set; }
    public List<PointOutput>? Points { get; set; }
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? W { get; set; }
    public float? H { get; set; }
    public bool? Filled { get; set; }
    [JsonPropertyName("note_text")]
    public string? NoteText { get; set; }
    public string? Text { get; set; }
    public List<AnnotationBlockOutput> OverlappingBlocks { get; set; } = [];
    public HeadingOutput? NearestHeading { get; set; }
}

public class RectOutput
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public RectOutput(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
}

public class PointOutput
{
    public float X { get; set; }
    public float Y { get; set; }
    public PointOutput(float x, float y) { X = x; Y = y; }
}

public class AnnotationBlockOutput
{
    public string Class { get; set; } = "";
    public int ClassId { get; set; }
    public BBoxOutput BBox { get; set; } = new();
    public float Confidence { get; set; }
    public string? Text { get; set; }
}

public class HeadingOutput
{
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public int Page { get; set; }
}

public class BookmarkOutput
{
    public string Name { get; set; } = "";
    public int Page { get; set; }
}
