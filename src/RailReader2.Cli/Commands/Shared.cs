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

    internal static string? ExtractTextInRect(PageText pageText, float left, float top, float right, float bottom)
    {
        var chars = new List<(int Index, char Ch)>();
        foreach (var cb in pageText.CharBoxes)
        {
            float midX = (cb.Left + cb.Right) / 2f;
            float midY = (cb.Top + cb.Bottom) / 2f;
            if (midX >= left && midX <= right && midY >= top && midY <= bottom
                && cb.Index >= 0 && cb.Index < pageText.Text.Length)
            {
                chars.Add((cb.Index, pageText.Text[cb.Index]));
            }
        }
        if (chars.Count == 0) return null;
        chars.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new string(chars.Select(c => c.Ch).ToArray()).Trim();
    }

    internal static string ExtractBlockText(PageText pageText, LayoutBlock block)
    {
        var bbox = block.BBox;
        return ExtractTextInRect(pageText, bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H) ?? "";
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
