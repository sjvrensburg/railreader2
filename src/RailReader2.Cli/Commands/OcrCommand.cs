using System.Diagnostics;
using RailReader.Core;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using RailReader2.Services;
using RapidOcrNet;

namespace RailReader.Cli.Commands;

/// <summary>
/// <c>ocr</c>: recognise a scanned PDF once and save the text as an invisible layer in a copy
/// (railreader2#273), so the copy is searchable, selectable and highlightable in any reader.
/// </summary>
public static class OcrCommand
{
    // --pack value → OcrModelRegistry id; "default" is the bundled PP-OCRv5 Latin set.
    private static readonly Dictionary<string, string?> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = null,
        ["tiny"] = OcrModelRegistry.PPOCRv6Tiny.Id,
        ["small"] = OcrModelRegistry.PPOCRv6Small.Id,
        ["medium"] = OcrModelRegistry.PPOCRv6Medium.Id,
    };

    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var pdfPath = Program.GetRequiredPdf(args);
        var outputPath = Path.GetFullPath(Program.GetOption(args, "output") ?? OcrTextLayer.DefaultOutputPath(pdfPath));
        var password = Program.GetOption(args, "password");
        var packName = Program.GetOption(args, "pack") ?? "default";
        int dpi = Shared.ParseClampedInt(Program.GetOption(args, "dpi"), 72, 600, OcrRecognizer.DefaultDpi, "--dpi");

        if (!Packs.TryGetValue(packName, out var packId))
            return Program.Fail($"Unknown --pack '{packName}'. Use one of: {string.Join(", ", Packs.Keys)}.");
        RapidOcrModelSet? models = null;
        if (packId is not null)
        {
            var desc = OcrModelRegistry.ById(packId)!;
            models = OcrModelLocator.Locate(desc.ModelSet);
            if (models is null)
                return Program.Fail($"The {desc.DisplayName} pack is not installed. Download it in RailReader2 " +
                    "(Settings ▸ Scanned Pages, advanced view) or place its files in a models/ directory.");
        }
        if (string.Equals(outputPath, pdfPath, StringComparison.Ordinal))
            return Program.Fail("--output must differ from the source PDF: the text layer is written to a copy.");

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

        using (pdf as IDisposable)
        {
            var pages = OcrTextLayer.PagesNeedingOcr(pdf, factory.CreatePdfTextService());
            if (pages.Count == 0)
            {
                Console.Error.WriteLine("Every page already has a text layer; nothing to do.");
                return 0;
            }
            Console.Error.WriteLine($"Recognising {pages.Count} of {pdf.PageCount} pages ({packName} pack, {dpi} dpi)...");

            using var ocr = new RapidOcrService(models, logger: logger);
            var stopwatch = Stopwatch.StartNew();
            var progress = new SyncProgress<(int Done, int Total)>(p =>
            {
                if (p.Done > 0) Console.Error.WriteLine($"  [{p.Done}/{p.Total}] {stopwatch.Elapsed.TotalSeconds / p.Done:F1} s/page");
            });
            var result = OcrTextLayer.Run(pdf, pdfPath, ocr, pages, outputPath, dpi, progress);

            if (result.PagesWritten == 0)
            {
                Console.Error.WriteLine("No text was recognised; no copy written.");
                return 0;
            }
            Console.Error.WriteLine($"Wrote {outputPath}: text layer on {result.PagesWritten} page(s), " +
                $"{result.PagesSkipped} skipped, {stopwatch.Elapsed.TotalSeconds:F0} s.");
            if (result.SourceWasSigned)
                Console.Error.WriteLine("Warning: the source is digitally signed; the copy's signatures are no longer valid.");
            return 0;
        }
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli ocr — Add an invisible OCR text layer to a copy of a PDF");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli ocr <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Recognises the pages that have no text layer and writes the text, invisibly, over the");
        Console.WriteLine("printed words in a copy, so it can be searched, selected and highlighted in any reader.");
        Console.WriteLine("Pages that already have text are left as they are; re-running replaces an earlier layer.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output <path>      Copy to write (default: <name>_ocr.pdf beside the source)");
        Console.WriteLine("  --pack <name>        OCR models: default (bundled, Latin script), tiny, small, medium");
        Console.WriteLine("                       (PP-OCRv6 multilingual; download in the app first). Bigger packs");
        Console.WriteLine("                       read better but are much slower.");
        Console.WriteLine("  --dpi <n>            Rasterisation resolution, 72-600 (default: 300)");
        Console.WriteLine("  --password <pwd>     Password for an encrypted PDF (the copy stays encrypted)");
    }
}
