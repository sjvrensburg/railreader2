using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
using RailReader2.References;

namespace RailReader.Cli.Commands;

/// <summary>
/// <c>references</c>: runs the same figure/table reference resolver the rail's auto-pin uses over a
/// whole PDF and reports every mention, the block it resolved to, or that it abstained. With
/// <c>--ground-truth</c> it also scores precision/recall per kind against labels and against two naive
/// baselines, so heuristic changes are measured rather than eyeballed.
/// </summary>
public static class ReferencesCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var (pdfPath, pdf) = Shared.OpenPdf(args, factory);
        var outputPath = Program.GetOption(args, "output");
        var truthPath = Program.GetOption(args, "ground-truth");
        var layout = Program.GetOption(args, "layout");
        double? minPrecision = null;
        if (Program.GetOption(args, "min-precision") is { } mp)
        {
            if (!double.TryParse(mp, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) || v is < 0 or > 1)
                return Program.Fail($"Invalid --min-precision: {mp} (expected 0-1)");
            minPrecision = v;
        }

        LayoutModelChoice.Builtin? choice = null;
        if (layout != null)
        {
            choice = layout.ToLowerInvariant() switch
            {
                "heron" => LayoutModelChoice.Builtin.Heron,
                "ppdoclayout" or "ppdoclayoutv3" => LayoutModelChoice.Builtin.PpDocLayoutV3,
                "pps" or "ppdoclayout-s" => LayoutModelChoice.Builtin.PpDocLayoutS,
                _ => null,
            };
            if (choice is null)
                return Program.Fail($"Invalid --layout: {layout} (expected: heron, ppdoclayout, pps)");
        }

        GroundTruth? truth = null;
        if (truthPath != null)
        {
            if (!File.Exists(truthPath)) return Program.Fail($"Ground truth not found: {truthPath}");
            truth = JsonSerializer.Deserialize(File.ReadAllText(truthPath), CliJsonContext.Default.GroundTruth)
                ?? throw new InvalidOperationException("Ground truth file is empty.");
        }

        var (pages, rangeError) = PageRangeParser.Parse(Program.GetOption(args, "pages"), pdf.PageCount);
        if (rangeError != null)
            return Program.Fail(rangeError);

        using var analyzer = Shared.CreateAnalyzer(true, choice);
        if (analyzer is null)
            return Program.Fail("Reference resolution needs a layout model; none found.");

        var textService = factory.CreatePdfTextService();
        var doc = new CliReferenceDocument(pdf.PageCount);
        foreach (var pageIdx in pages!)
        {
            Console.Error.WriteLine($"  Analysing page {pageIdx + 1}/{pdf.PageCount}...");
            var (pw, ph) = pdf.GetPageSize(pageIdx);
            var pageText = textService.ExtractPageText(pdf.PdfBytes, pageIdx);
            var (rgb, pxW, pxH) = pdf.RenderPagePixmap(pageIdx, analyzer.Capabilities.InputSize);
            var analysis = LayoutAnalysisPipeline.RunWithPixmap(analyzer, rgb, pxW, pxH, pw, ph, pageText.CharBoxes);
            doc.Add(pageIdx, analysis, pageText);
        }

        var mentions = ReferenceBenchmark.Scan(doc, new ReferenceIndex());
        var report = new ReferenceReport
        {
            Source = Path.GetFileName(pdfPath),
            Layout = layout ?? choice?.ToString() ?? LayoutModelChoice.LoadChoice().ToString(),
            PageCount = pdf.PageCount,
            Mentions = mentions,
        };

        if (truth != null)
        {
            report.Metrics = ReferenceBenchmark.Score(mentions, truth, report.Failures);
            foreach (var (name, nearest) in new[] { ("first_match", false), ("nearest_match", true) })
            {
                var baseline = ReferenceBenchmark.NaiveBaseline(doc, mentions, nearest);
                report.Baselines[name] = ReferenceBenchmark.Score(baseline, truth, failures: null);
            }
        }

        Shared.WriteJsonOutput(report, outputPath, CliJsonContext.Default.ReferenceReport, "References");
        if (report.Metrics is { } scored)
        {
            Console.Error.WriteLine($"  resolver       {Summarise(scored.Overall)}");
            foreach (var (name, m) in report.Baselines)
                Console.Error.WriteLine($"  {name,-14} {Summarise(m.Overall)}");
            foreach (var failure in report.Failures)
                Console.Error.WriteLine($"  miss: {failure}");
        }

        if (minPrecision is { } min)
        {
            if (report.Metrics is null)
                return Program.Fail("--min-precision needs --ground-truth.");
            var precision = report.Metrics.Overall.Precision;
            if (precision < min)
            {
                Console.Error.WriteLine($"Precision {precision:F4} is below the required {min:F4}.");
                return 2;
            }
        }
        return 0;
    }

    static string Summarise(Metrics m) =>
        $"P={m.Precision:F2} R={m.Recall:F2} F1={m.F1:F2} (tp {m.TruePositives}, fp {m.FalsePositives}, fn {m.FalseNegatives})";

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli references — Detect figure/table mentions and resolve them to targets");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli references <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --pages <range>          Pages to analyse (default: all)");
        Console.WriteLine("  --layout <model>         heron | ppdoclayout | pps (default: your configured model)");
        Console.WriteLine("  --ground-truth <path>    Labels JSON; adds precision/recall/F1, failures and baselines");
        Console.WriteLine("  --min-precision <0-1>    Exit with code 2 if overall precision is lower (needs --ground-truth)");
        Console.WriteLine("  --output <path>          Output JSON file path (default: stdout)");
        Console.WriteLine();
        Console.WriteLine("Ground truth: {\"mentions\":[{\"page\":2,\"kind\":\"Figure\",\"number\":\"3\",");
        Console.WriteLine("  \"target_page\":1,\"target_block\":4}], \"empty_pages\":[5]}  (pages are 0-based;");
        Console.WriteLine("  omit target_* to require abstention; empty_pages must resolve nothing)");
    }

    private sealed class CliReferenceDocument(int pageCount) : IReferenceDocument
    {
        private readonly Dictionary<int, (PageAnalysis Analysis, PageText Text)> _pages = [];

        public void Add(int page, PageAnalysis analysis, PageText text) => _pages[page] = (analysis, text);

        public int PageCount => pageCount;
        public int AnalysedPageCount => _pages.Count;

        public bool TryGetAnalysis(int page, out PageAnalysis analysis)
        {
            bool found = _pages.TryGetValue(page, out var entry);
            analysis = entry.Analysis;
            return found;
        }

        public PageText GetText(int page) => _pages[page].Text;
    }
}
