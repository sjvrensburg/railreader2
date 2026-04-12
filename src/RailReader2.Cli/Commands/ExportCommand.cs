using RailReader.Core;
using RailReader.Core.Services;
using RailReader.Export;

namespace RailReader.Cli.Commands;

public static class ExportCommand
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
        var pageRange = Program.GetOption(args, "pages");
        var figureDir = Program.GetOption(args, "figure-dir");
        var concurrencyStr = Program.GetOption(args, "concurrency");
        var endpointOverride = Program.GetOption(args, "endpoint");
        var modelOverride = Program.GetOption(args, "model");
        var apiKeyOverride = Program.GetOption(args, "api-key");
        var promptStyleStr = Program.GetOption(args, "prompt-style");
        var noVlm = Program.HasFlag(args, "no-vlm");
        var noAnnotations = Program.HasFlag(args, "no-annotations");
        var noPageBreaks = Program.HasFlag(args, "no-page-breaks");
        var noStructured = Program.HasFlag(args, "no-structured-output");

        int concurrency = 2;
        if (concurrencyStr != null && int.TryParse(concurrencyStr, out var c))
            concurrency = Math.Max(1, c);

        var promptStyle = VlmService.PromptStyle.Instruction;
        if (promptStyleStr != null
            && !Enum.TryParse<VlmService.PromptStyle>(promptStyleStr, ignoreCase: true, out promptStyle))
            return Program.Fail($"Invalid --prompt-style: {promptStyleStr} (expected: instruction, ocr)");

        // Resolve VLM endpoint
        VlmEndpointConfig? vlmEndpoint = null;
        if (!noVlm)
        {
            var appConfig = AppConfig.Load();
            var apiKey = apiKeyOverride
                ?? (string.IsNullOrWhiteSpace(appConfig.VlmApiKey)
                    ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    : appConfig.VlmApiKey);
            vlmEndpoint = new VlmEndpointConfig(
                endpointOverride ?? appConfig.VlmEndpoint,
                modelOverride ?? appConfig.VlmModel,
                apiKey);
        }

        var options = new MarkdownExportOptions
        {
            EnableVlm = !noVlm,
            IncludeAnnotations = !noAnnotations,
            IncludeFigureImages = figureDir != null,
            InsertPageBreaks = !noPageBreaks,
            FigureOutputDir = figureDir,
            PageRange = pageRange,
            VlmConcurrency = concurrency,
            VlmEndpoint = vlmEndpoint,
            VlmPromptStyle = promptStyle,
            VlmStructuredOutput = !noStructured,
        };

        var service = new MarkdownExportService(factory, logger);
        var progress = new ConsoleExportProgress();

        TextWriter output;
        StreamWriter? fileWriter = null;
        if (outputPath != null)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            fileWriter = new StreamWriter(outputPath, append: false, encoding: new System.Text.UTF8Encoding(false));
            output = fileWriter;
        }
        else
        {
            output = Console.Out;
        }

        try
        {
            service.ExportAsync(pdfPath, output, options, progress, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            fileWriter?.Dispose();
        }

        if (outputPath != null)
            Console.Error.WriteLine($"Markdown exported to {Path.GetFullPath(outputPath)}");

        return 0;
    }

    private sealed class ConsoleExportProgress : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value)
        {
            Console.Error.WriteLine($"  [{value.CurrentPage}/{value.TotalPages}] {value.Status}");
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli export — Export PDF to structured Markdown");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli export <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Output:");
        Console.WriteLine("  --output <path>             Markdown output file (default: stdout)");
        Console.WriteLine("  --pages <range>             Page range (e.g. 1,3,5-10)");
        Console.WriteLine("  --no-page-breaks            Omit page break markers (---)");
        Console.WriteLine();
        Console.WriteLine("Content:");
        Console.WriteLine("  --no-vlm                    Disable VLM transcription");
        Console.WriteLine("  --no-annotations            Exclude annotations");
        Console.WriteLine("  --figure-dir <dir>          Save figure PNGs (referenced in markdown)");
        Console.WriteLine();
        Console.WriteLine("VLM config (override AppConfig):");
        Console.WriteLine("  --endpoint <url>            OpenAI-compatible endpoint");
        Console.WriteLine("  --model <name>              Model identifier");
        Console.WriteLine("  --api-key <key>             API key (else $OPENAI_API_KEY, else AppConfig)");
        Console.WriteLine("  --concurrency <n>           Parallel VLM requests (default: 2)");
        Console.WriteLine("  --prompt-style <style>      instruction (default) | ocr");
        Console.WriteLine("  --no-structured-output      Disable JSON schema response format");
        Console.WriteLine();
        Console.WriteLine("Graceful degradation:");
        Console.WriteLine("  With ONNX + VLM:     Full fidelity (headings, LaTeX, tables, figures)");
        Console.WriteLine("  With ONNX only:      Headings + text + [equation]/[figure]/code tables");
        Console.WriteLine("  Without ONNX:        Plain text per page with outline heading markers");
    }
}
