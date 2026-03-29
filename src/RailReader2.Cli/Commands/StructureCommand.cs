using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader.Cli.Commands;

public static class StructureCommand
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
        var includeText = Program.HasFlag(args, "include-text");
        var analyze = Program.HasFlag(args, "analyze");

        var pdf = factory.CreatePdfService(pdfPath);
        var pageRange = Program.GetOption(args, "pages");
        var (pages, rangeError) = PageRangeParser.Parse(pageRange, pdf.PageCount);
        if (rangeError != null)
            return Program.Fail(rangeError);

        LayoutAnalyzer? analyzer = null;
        if (analyze)
        {
            var modelPath = DocumentController.FindModelPath();
            if (modelPath == null)
            {
                Console.Error.WriteLine("Warning: ONNX model not found. Skipping layout analysis.");
                Console.Error.WriteLine("  Download the model with: ./scripts/download-model.sh");
                analyze = false;
            }
            else
            {
                analyzer = new LayoutAnalyzer(modelPath);
            }
        }

        IPdfTextService? textService = null;
        if (includeText)
            textService = factory.CreatePdfTextService();

        var result = new StructureOutput
        {
            Source = Path.GetFileName(pdfPath),
            PageCount = pdf.PageCount,
            Outline = pdf.Outline.Select(SerializeOutlineEntry).ToList()
        };

        foreach (var pageIdx in pages!)
        {
            if (!analyze && !includeText) continue;

            Console.Error.WriteLine($"  Processing page {pageIdx + 1}/{pdf.PageCount}...");

            var (pw, ph) = pdf.GetPageSize(pageIdx);
            PageAnalysis? analysis = null;

            if (analyze && analyzer != null)
            {
                var (rgbBytes, pxW, pxH) = pdf.RenderPagePixmap(pageIdx, 800);
                analysis = analyzer.RunAnalysis(rgbBytes, pxW, pxH, pw, ph);
            }

            PageText? pageText = null;
            if (includeText && textService != null)
                pageText = textService.ExtractPageText(pdf.PdfBytes, pageIdx);

            if (analysis != null || pageText != null)
            {
                var pageOutput = new StructurePage
                {
                    Page = pageIdx,
                    Width = (float)pw,
                    Height = (float)ph
                };

                if (analysis != null)
                {
                    pageOutput.Blocks = analysis.Blocks.Select(b =>
                    {
                        var block = new StructureBlock
                        {
                            Class = b.ClassId < LayoutConstants.LayoutClasses.Length
                                ? LayoutConstants.LayoutClasses[b.ClassId]
                                : $"class_{b.ClassId}",
                            ClassId = b.ClassId,
                            BBox = new BBoxOutput(b.BBox.X, b.BBox.Y, b.BBox.W, b.BBox.H),
                            Confidence = b.Confidence,
                            ReadingOrder = b.Order,
                        };

                        if (includeText && pageText != null)
                            block.Text = ExtractBlockText(pageText, b);

                        return block;
                    }).ToList();
                }

                result.Pages.Add(pageOutput);
            }
        }

        analyzer?.Dispose();

        var json = JsonSerializer.Serialize(result, s_jsonOptions);

        if (outputPath != null)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);
            Console.Error.WriteLine($"Structure written to {Path.GetFullPath(outputPath)}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return 0;
    }

    internal static string ExtractBlockText(PageText pageText, LayoutBlock block)
    {
        var bbox = block.BBox;
        var chars = new List<(int Index, char Ch)>();
        foreach (var cb in pageText.CharBoxes)
        {
            float midX = (cb.Left + cb.Right) / 2f;
            float midY = (cb.Top + cb.Bottom) / 2f;
            if (midX >= bbox.X && midX <= bbox.X + bbox.W
                && midY >= bbox.Y && midY <= bbox.Y + bbox.H
                && cb.Index >= 0 && cb.Index < pageText.Text.Length)
            {
                chars.Add((cb.Index, pageText.Text[cb.Index]));
            }
        }
        if (chars.Count == 0) return "";
        chars.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new string(chars.Select(c => c.Ch).ToArray()).Trim();
    }

    internal static OutlineEntryOutput SerializeOutlineEntry(OutlineEntry entry) => new()
    {
        Title = entry.Title,
        Page = entry.Page,
        Children = entry.Children.Select(SerializeOutlineEntry).ToList()
    };

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli structure — Extract document structure as JSON");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli structure <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --pages <range>       Page range for layout analysis (default: all)");
        Console.WriteLine("  --output <path>       Output JSON file path (default: stdout)");
        Console.WriteLine("  --include-text        Include extracted text per layout block");
        Console.WriteLine("  --analyze             Run ONNX layout analysis to detect blocks");
    }

    static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// Output DTOs
public class StructureOutput
{
    public string Source { get; set; } = "";
    public int PageCount { get; set; }
    public List<OutlineEntryOutput> Outline { get; set; } = [];
    public List<StructurePage> Pages { get; set; } = [];
}

public class OutlineEntryOutput
{
    public string Title { get; set; } = "";
    public int? Page { get; set; }
    public List<OutlineEntryOutput> Children { get; set; } = [];
}

public class StructurePage
{
    public int Page { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public List<StructureBlock> Blocks { get; set; } = [];
}

public class StructureBlock
{
    public string Class { get; set; } = "";
    public int ClassId { get; set; }
    public BBoxOutput BBox { get; set; } = new();
    public float Confidence { get; set; }
    public int ReadingOrder { get; set; }
    public string? Text { get; set; }
}

public class BBoxOutput
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    public BBoxOutput() { }
    public BBoxOutput(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
}
