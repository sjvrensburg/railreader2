using RailReader.Core;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader.Cli;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
            return PrintHelp();

        // Bootstrap: set the central logger (services read from RailReaderLogging.Logger)
        var logger = new ConsoleLogger();
        RailReaderLogging.Logger = logger;

        // Read the app's sidecar annotations under the same content-identity key it writes them with
        // (read-only lookup: never touches the app's identity map).
        AnnotationService.Default.DocumentKeyResolver = DocumentIdentity.ReadOnlyResolver;

        var factory = new SkiaPdfServiceFactory();

        bool verbose = HasFlag(args, "verbose");

        try
        {
            return args[0] switch
            {
                "render" => Commands.RenderCommand.Execute(args[1..], factory, logger),
                "structure" => Commands.StructureCommand.Execute(args[1..], factory, logger),
                "annotations" => Commands.AnnotationsCommand.Execute(args[1..], factory, logger),
                "vlm" => Commands.VlmCommand.Execute(args[1..], factory, logger),
                "references" => Commands.ReferencesCommand.Execute(args[1..], factory, logger),
                "export" => Commands.ExportCommand.Execute(args[1..], factory, logger),
                "ocr" => Commands.OcrCommand.Execute(args[1..], factory, logger),
                "package" => Commands.PackageCommand.Execute(args[1..], factory, logger),
                "unpack" => Commands.UnpackCommand.Execute(args[1..], factory, logger),
                _ => Fail($"Unknown command: '{args[0]}'. Run with --help for usage.")
            };
        }
        catch (Exception ex)
        {
            return Fail(verbose ? ex.ToString() : ex.Message);
        }
    }

    static int PrintHelp()
    {
        Console.WriteLine("railreader2-cli — headless PDF extraction tool for RailReader2");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli <command> <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  render <pdf>          Render pages as PNG with optional filters");
        Console.WriteLine("  structure <pdf>       Extract document structure as JSON");
        Console.WriteLine("  annotations <pdf>     Export annotations as JSON or PDF");
        Console.WriteLine("  vlm <pdf>             Transcribe equations/tables/figures via a vision LLM");
        Console.WriteLine("  export <pdf>          Export PDF to structured Markdown");
        Console.WriteLine("  references <pdf>      Detect figure/table mentions, resolve them, and benchmark against labels");
        Console.WriteLine("  ocr <pdf>             Add an invisible OCR text layer to a copy of a scanned PDF");
        Console.WriteLine("  package <pdf>         Bundle a PDF and its reading session into a .railreading file");
        Console.WriteLine("  unpack <package>      Extract a .railreading package");
        Console.WriteLine();
        Console.WriteLine("Run 'railreader2-cli <command> --help' for command-specific options.");
        return 0;
    }

    internal static int Fail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    internal static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == $"--{name}")
                return args[i + 1];
        }
        return null;
    }

    internal static bool HasFlag(string[] args, string name)
    {
        return args.Contains($"--{name}");
    }

    /// <summary>True when the user asked for help via <c>--help</c> or <c>-h</c>. Centralised (rather
    /// than each subcommand checking <c>HasFlag(args, "-h")</c> — which resolves to the wrong
    /// "---h" via the long-flag convention above) so a future tweak to help-flag matching is a
    /// one-line change instead of five identical edits.</summary>
    internal static bool HasHelpFlag(string[] args)
        => HasFlag(args, "help") || args.Contains("-h");

    // Options that consume the following token as their value (every GetOption call site).
    // GetRequiredPdf must skip those values or `--output-dir out in.pdf` mistakes "out" for the PDF.
    private static readonly HashSet<string> ValueOptions =
    [
        "output", "output-dir", "pages", "page", "block", "dpi", "effect", "intensity",
        "format", "password", "figure-dir", "concurrency", "endpoint", "model", "api-key",
        "equation-endpoint", "equation-model", "equation-api-key",
        "table-endpoint", "table-model", "table-api-key",
        "figure-endpoint", "figure-model", "figure-api-key",
        "prompt-style", "dump-crops", "min-confidence", "from-structure",
        "ground-truth", "layout", "min-precision", "classes", "pack",
    ];

    internal static string GetRequiredPdf(string[] args, string kind = "PDF")
    {
        string? pdfPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (ValueOptions.Contains(a[2..])) i++; // skip this option's value
                continue;
            }
            if (a.StartsWith('-')) continue;
            pdfPath = a;
            break;
        }
        if (pdfPath is null)
            throw new InvalidOperationException($"{kind} path is required as the first argument.");

        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"{kind} not found: {pdfPath}");

        return Path.GetFullPath(pdfPath);
    }
}
