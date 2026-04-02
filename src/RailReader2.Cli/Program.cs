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

        // Bootstrap: set static loggers (internals visible to this project)
        var logger = new ConsoleLogger();
        SkiaPdfService.Logger = logger;
        AnnotationService.Logger = logger;
        LayoutAnalyzer.Logger = logger;
        PdfTextService.Logger = logger;
        PdfOutlineExtractor.Logger = logger;

        var factory = new SkiaPdfServiceFactory();

        bool verbose = HasFlag(args, "verbose");

        try
        {
            return args[0] switch
            {
                "render" => Commands.RenderCommand.Execute(args[1..], factory, logger),
                "structure" => Commands.StructureCommand.Execute(args[1..], factory, logger),
                "annotations" => Commands.AnnotationsCommand.Execute(args[1..], factory, logger),
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

    internal static string GetRequiredPdf(string[] args)
    {
        var pdfPath = args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? throw new InvalidOperationException("PDF path is required as the first argument.");

        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"PDF not found: {pdfPath}");

        return Path.GetFullPath(pdfPath);
    }
}
