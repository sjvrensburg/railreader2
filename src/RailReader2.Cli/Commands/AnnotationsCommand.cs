using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
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
        var password = Program.GetOption(args, "password");
        var format = Program.GetOption(args, "format") ?? "json";
        var pageRange = Program.GetOption(args, "pages");
        var includeText = Program.HasFlag(args, "include-text");
        var includeBlocks = Program.HasFlag(args, "include-blocks");

        if (format is not "json" and not "pdf")
            return Program.Fail($"Unknown format '{format}'. Valid options: json, pdf");

        var annotations = CompositeAnnotationStore.Default.Load(pdfPath);
        if (annotations == null)
        {
            Console.Error.WriteLine("No annotations found for this document.");
            return 0;
        }

        if (format == "pdf")
        {
            var outPath = outputPath ?? Path.ChangeExtension(Path.GetFileName(pdfPath), ".annotated.pdf");
            IPdfService pdf;
            try
            {
                pdf = factory.CreatePdfService(pdfPath, password);
            }
            catch (PdfPasswordRequiredException ex)
            {
                return Program.Fail(ex.WrongPassword
                    ? "Incorrect password. Pass the correct --password for this encrypted PDF."
                    : "This PDF is password-protected. Pass --password <pwd> to open it.");
            }
            try
            {
                AnnotationExportService.Export(pdf, annotations, outPath);
            }
            catch (InvalidOperationException ex)
            {
                // Export throws InvalidOperationException both for the encrypted-source refusal and
                // for in-PDFium load/save failures. Only the former carries a password; otherwise
                // surface the real error rather than the (wrong) encryption message.
                if (!string.IsNullOrEmpty(pdf.Password))
                    // Flattening drops encryption, so Core refuses an encrypted source. Annotate in
                    // place instead (annotations are already written into the encrypted PDF).
                    return Program.Fail(
                        "Cannot export an encrypted PDF to a flattened copy — the result would be unencrypted. " +
                        "The annotations are already saved inside the original encrypted PDF.");
                return Program.Fail($"Failed to export annotated PDF: {ex.Message}");
            }
            Console.Error.WriteLine($"Annotated PDF written to {Path.GetFullPath(outPath)}");
            return 0;
        }

        IPdfService pdf2;
        try
        {
            pdf2 = factory.CreatePdfService(pdfPath, password);
        }
        catch (PdfPasswordRequiredException ex)
        {
            return Program.Fail(ex.WrongPassword
                ? "Incorrect password. Pass the correct --password for this encrypted PDF."
                : "This PDF is password-protected. Pass --password <pwd> to open it.");
        }

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

                PageText? pageText = null;
                if ((includeText || includeBlocks) && textService != null)
                    pageText = textService.ExtractPageText(pdf2.PdfBytes, pageIdx);

                PageAnalysis? analysis = null;
                if (includeBlocks && analyzer != null)
                {
                    var (rgbBytes, pxW, pxH) = pdf2.RenderPagePixmap(pageIdx, analyzer.Capabilities.InputSize);
                    analysis = LayoutAnalysisPipeline.RunWithPixmap(
                        analyzer, rgbBytes, pxW, pxH, pw, ph, pageText?.CharBoxes);
                }

                if (!includeText) pageText = null;

                // The nearest outline heading depends only on the page, not the
                // individual annotation — compute it once instead of per annotation.
                var pageOutlineHeading = FindNearestOutlineHeading(pdf2.Outline, pageIdx);

                foreach (var ann in pageAnnotations)
                {
                    var annOutput = SerializeAnnotation(ann);
                    var annBounds = AnnotationGeometry.GetAnnotationBounds(ann);

                    if (includeText && pageText != null && annBounds is { } textRect)
                        annOutput.Text = pageText.ExtractTextInRect(textRect.Left, textRect.Top, textRect.Right, textRect.Bottom);

                    if (includeBlocks && analysis != null && annBounds is { } blockRect)
                    {
                        var classTable = analyzer!.Capabilities.Classes;
                        foreach (var block in analysis.Blocks)
                        {
                            if (Overlaps(blockRect, block.BBox))
                            {
                                var blockOutput = new AnnotationBlockOutput
                                {
                                    Class = block.ClassId >= 0 && block.ClassId < classTable.Count
                                        ? classTable[block.ClassId].Name
                                        : $"class_{block.ClassId}",
                                    ClassId = block.ClassId,
                                    Role = block.Role.ToString(),
                                    BBox = new BBoxOutput(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H),
                                    Confidence = block.Confidence
                                };

                                if (includeText && pageText != null)
                                    blockOutput.Text = pageText.ExtractBlockText(block);

                                annOutput.OverlappingBlocks.Add(blockOutput);
                            }
                        }
                    }

                    annOutput.NearestHeading = FindNearestHeading(annBounds, pageIdx, pageOutlineHeading, analysis);
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

        Shared.WriteJsonOutput(result, outputPath, CliJsonContext.Default.AnnotationExportOutput, "Annotations");
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
        // Subtype-specific geometry first; shared metadata is applied below for every subtype.
        var output = ann switch
        {
            // Highlight/Underline/StrikeOut/Squiggly all derive from TextMarkupAnnotation (Rects).
            TextMarkupAnnotation m => new AnnotationOutput
            {
                Type = TextMarkupType(m),
                Rects = m.Rects.Select(r => new RectOutput(r.X, r.Y, r.W, r.H)).ToList()
            },
            FreehandAnnotation f => new AnnotationOutput
            {
                Type = "freehand",
                StrokeWidth = f.StrokeWidth,
                Points = f.Points.Select(p => new PointOutput(p.X, p.Y)).ToList()
            },
            TextNoteAnnotation t => new AnnotationOutput
            {
                Type = "text_note",
                X = t.X, Y = t.Y, NoteText = t.Text
            },
            RectAnnotation r => new AnnotationOutput
            {
                Type = "rect",
                X = r.X, Y = r.Y, W = r.W, H = r.H,
                StrokeWidth = r.StrokeWidth, Filled = r.Filled
            },
            // Caret is read-only (PDFium can't create them); FreeText's text lives in Contents.
            CaretAnnotation c => new AnnotationOutput
            {
                Type = "caret",
                X = c.X, Y = c.Y, W = c.W, H = c.H
            },
            FreeTextAnnotation ft => new AnnotationOutput
            {
                Type = "free_text",
                X = ft.X, Y = ft.Y, W = ft.W, H = ft.H
            },
            _ => new AnnotationOutput { Type = "unknown" }
        };

        output.Color = ann.Color;
        output.Opacity = ann.Opacity;
        // Round-trip metadata (null fields are omitted via WhenWritingNull).
        output.Author = ann.Author;
        output.Contents = ann.Contents;
        output.Subject = ann.Subject;
        output.NativeId = ann.NativeId;
        output.CreatedUtc = ann.CreatedUtc;
        output.ModifiedUtc = ann.ModifiedUtc;
        output.State = ann.State == ReviewState.None ? null : ann.State.ToString();
        output.Source = ann.Source.ToString();
        output.InReplyTo = ann.InReplyTo;
        return output;
    }

    static string TextMarkupType(TextMarkupAnnotation m) => m switch
    {
        UnderlineAnnotation => "underline",
        StrikeOutAnnotation => "strikeout",
        SquigglyAnnotation => "squiggly",
        _ => "highlight",
    };

    static HeadingOutput? FindNearestHeading(RectF? annBounds, int pageIdx,
        HeadingOutput? pageOutlineHeading, PageAnalysis? analysis)
    {
        if (annBounds is not { } annRect) return null;

        if (pageOutlineHeading != null)
            return pageOutlineHeading;

        if (analysis == null) return null;

        LayoutBlock? bestBlock = null;
        float bestDist = float.MaxValue;
        foreach (var block in analysis.Blocks)
        {
            if (block.Role is BlockRole.Title or BlockRole.Heading)
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
                Title = bestBlock.Role.ToString(),
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
        Console.WriteLine("  --password <pwd>      Password for an encrypted PDF");
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
    // Native-PDF round-trip metadata (0.17.0). Null fields are omitted.
    public string? Author { get; set; }
    public string? Contents { get; set; }
    public string? Subject { get; set; }
    public string? NativeId { get; set; }
    public DateTimeOffset? CreatedUtc { get; set; }
    public DateTimeOffset? ModifiedUtc { get; set; }
    public string? State { get; set; }
    public string? Source { get; set; }
    public string? InReplyTo { get; set; }
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
    public string Role { get; set; } = "";
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
