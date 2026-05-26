using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Core.Vlm.OpenAI;

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
    /// Parses an integer CLI option, clamps it to [<paramref name="min"/>, <paramref name="max"/>],
    /// and prints a warning if the value was clamped. Returns <paramref name="default"/> when the
    /// option is absent or unparseable.
    /// </summary>
    internal static int ParseClampedInt(string? raw, int min, int max, int @default, string optionName)
    {
        if (raw == null || !int.TryParse(raw, out var v)) return @default;
        var clamped = Math.Clamp(v, min, max);
        if (v != clamped)
            Console.Error.WriteLine($"Warning: {optionName} clamped to {clamped} (valid range: {min}-{max})");
        return clamped;
    }

    /// <summary>
    /// Parses a float CLI option, clamps it to [<paramref name="min"/>, <paramref name="max"/>],
    /// and prints a warning if the value was clamped. Returns <paramref name="default"/> when the
    /// option is absent or unparseable.
    /// </summary>
    internal static float ParseClampedFloat(string? raw, float min, float max, float @default, string optionName)
    {
        if (raw == null || !float.TryParse(raw, out var v)) return @default;
        var clamped = Math.Clamp(v, min, max);
        if (v != clamped)
            Console.Error.WriteLine($"Warning: {optionName} clamped to {clamped:F1} (valid range: {min}-{max})");
        return clamped;
    }

    /// <summary>
    /// Parses a positive-integer CLI option (concurrency, count). Returns <paramref name="default"/>
    /// when absent or unparseable; floors to <paramref name="min"/> otherwise.
    /// </summary>
    internal static int ParsePositiveInt(string? raw, int @default, int min = 1)
    {
        if (raw == null || !int.TryParse(raw, out var v)) return @default;
        return Math.Max(min, v);
    }

    /// <summary>
    /// Parses the <c>--prompt-style</c> option. Returns a (style, error) tuple; the error is
    /// populated when the value can't be parsed so the caller can <c>return Program.Fail(err)</c>.
    /// </summary>
    internal static (VlmService.PromptStyle Style, string? Error) ParsePromptStyle(string? raw)
    {
        var style = VlmService.PromptStyle.Instruction;
        if (raw != null && !Enum.TryParse<VlmService.PromptStyle>(raw, ignoreCase: true, out style))
            return (style, $"Invalid --prompt-style: {raw} (expected: instruction, ocr)");
        return (style, null);
    }

    /// <summary>
    /// Writes a JSON-serialised <paramref name="output"/> to <paramref name="outputPath"/>
    /// (creating the directory if needed), or to stdout when the path is null. Prints
    /// "<paramref name="label"/> written to ..." on stderr when writing to a file.
    /// </summary>
    internal static void WriteJsonOutput<T>(T output, string? outputPath, JsonTypeInfo<T> typeInfo, string label)
    {
        var json = JsonSerializer.Serialize(output, typeInfo);

        if (outputPath != null)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);
            Console.Error.WriteLine($"{label} written to {Path.GetFullPath(outputPath)}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

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
