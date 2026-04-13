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
    };

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
    /// Creates a LayoutAnalyzer if the ONNX model is available.
    /// Returns null and prints a warning if the model is not found.
    /// </summary>
    internal static LayoutAnalyzer? CreateAnalyzer(bool requested)
    {
        if (!requested) return null;

        var modelPath = DocumentController.FindModelPath();
        if (modelPath == null)
        {
            Console.Error.WriteLine("Warning: ONNX model not found. Skipping layout analysis.");
            Console.Error.WriteLine("  Download the model with: ./scripts/download-model.sh");
            return null;
        }
        return new LayoutAnalyzer(modelPath);
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
