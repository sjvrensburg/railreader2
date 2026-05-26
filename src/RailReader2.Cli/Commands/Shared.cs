using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Cli.Commands;

internal static class Shared
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = CliJsonContext.Default,
    };

    /// <summary>
    /// Resolves a VLM endpoint configuration from CLI overrides, the persisted
    /// AppConfig, and the OPENAI_API_KEY environment variable.
    /// Precedence: explicit override > AppConfig > $OPENAI_API_KEY.
    /// </summary>
    internal static VlmEndpointConfig BuildVlmEndpoint(
        string? endpointOverride = null, string? modelOverride = null, string? apiKeyOverride = null)
    {
        var appConfig = AppConfig.Load();
        var apiKey = apiKeyOverride
            ?? (string.IsNullOrWhiteSpace(appConfig.VlmApiKey)
                ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                : appConfig.VlmApiKey);
        return new VlmEndpointConfig(
            endpointOverride ?? appConfig.VlmEndpoint,
            modelOverride ?? appConfig.VlmModel,
            apiKey);
    }

    internal static OutlineEntryOutput SerializeOutlineEntry(OutlineEntry entry) => new()
    {
        Title = entry.Title,
        Page = entry.Page,
        Children = entry.Children.Select(SerializeOutlineEntry).ToList()
    };

    /// <summary>
    /// Resolves the PDF path from args (first non-flag argument), verifies it exists,
    /// and opens the PDF via the factory. Throws if missing. Used as the first step
    /// in every command.
    /// </summary>
    internal static (string PdfPath, IPdfService Pdf) OpenPdf(string[] args, IPdfServiceFactory factory)
    {
        var pdfPath = Program.GetRequiredPdf(args);
        var pdf = factory.CreatePdfService(pdfPath);
        return (pdfPath, pdf);
    }

    /// <summary>
    /// Creates the layout analyzer chosen by the user's config (PP-DocLayoutV3
    /// by default, Heron or PP-DocLayout-S if selected and present), if the
    /// corresponding ONNX model is available. Returns null and prints a warning
    /// otherwise.
    /// </summary>
    internal static ILayoutAnalyzer? CreateAnalyzer(bool requested)
    {
        if (!requested) return null;

        var choice = LayoutModelChoice.LoadChoice();

        if (choice == LayoutModelChoice.Builtin.Heron)
        {
            var heronPath = LayoutModelChoice.FindHeronModelPath();
            if (heronPath != null)
            {
                return new HeronLayoutAnalyzer(heronPath, RailReader.Core.Analysis.DoclingHeronRoles.Capabilities);
            }
            Console.Error.WriteLine($"Warning: Docling Heron model not found ({LayoutModelChoice.HeronFileName}).");
            Console.Error.WriteLine("  See docs/heron-layout-model.md for download instructions.");
            Console.Error.WriteLine("  Falling back to PP-DocLayoutV3.");
            // fall through to PP
        }
        else if (choice == LayoutModelChoice.Builtin.PpDocLayoutS)
        {
            var ppsPath = LayoutModelChoice.FindPpsModelPath();
            if (ppsPath != null)
            {
                return new PPDocLayoutSLayoutAnalyzer(ppsPath, RailReader.Core.Analysis.PPDocLayoutSRoles.Capabilities);
            }
            Console.Error.WriteLine($"Warning: PP-DocLayout-S model not found ({LayoutModelChoice.PpsFileName}).");
            Console.Error.WriteLine("  See docs/pp-doclayout-s.md for download instructions.");
            Console.Error.WriteLine("  Falling back to PP-DocLayoutV3.");
            // fall through to PP
        }

        var modelPath = LayoutModelLocator.FindModelPath();
        if (modelPath == null)
        {
            Console.Error.WriteLine("Warning: ONNX model not found. Skipping layout analysis.");
            Console.Error.WriteLine("  Download the model with: ./scripts/download-model.sh");
            return null;
        }
        return new LayoutAnalyzer(modelPath, RailReader.Core.Analysis.PPDocLayoutV3Roles.Capabilities);
    }
}

public class OutlineEntryOutput
{
    public string Title { get; set; } = "";
    public int? Page { get; set; }
    public List<OutlineEntryOutput> Children { get; set; } = [];
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
