using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader2.RenderHarness.Headless;

/// <summary>Shared output settings plus the list of screenshots to generate.</summary>
public sealed class HeadlessConfig
{
    /// <summary>Output directory, relative to repo root.</summary>
    public string OutputDir { get; set; } = "docs/img";

    /// <summary>Window/canvas size in device-independent pixels.</summary>
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;

    /// <summary>"dark" or "light" — sets the app theme variant.</summary>
    public string Theme { get; set; } = "dark";

    public List<ShotSpec> Shots { get; set; } = [];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static HeadlessConfig Load(string path)
        => JsonSerializer.Deserialize<HeadlessConfig>(File.ReadAllText(path), Options)
           ?? throw new InvalidOperationException($"Empty or invalid config: {path}");
}

/// <summary>One screenshot: the input PDF plus the real-UI state to drive before capture.</summary>
public sealed class ShotSpec
{
    /// <summary>Output filename (no extension), e.g. "sidebar-navigation-active".</summary>
    public string Name { get; set; } = "screenshot";

    /// <summary>Source PDF path, relative to repo root.</summary>
    public string Pdf { get; set; } = "";

    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Absolute camera zoom (1.0 = 100%). 0 or omitted = fit page. A value above the
    /// app's rail-zoom threshold (≈3.0) engages rail mode automatically once the page
    /// has been analysed.
    /// </summary>
    public float Zoom { get; set; } = 0f;

    /// <summary>Which accordion side pane is open (none hides the panel).</summary>
    public Pane Sidebar { get; set; } = Pane.None;

    /// <summary>Which annotation tool is active.</summary>
    public Tool Tool { get; set; } = Tool.None;

    /// <summary>Wait for ONNX layout analysis of this page before capturing
    /// (needed for rail mode and the Figures/Index pane to be populated).</summary>
    public bool RequireAnalysis { get; set; } = false;
}

/// <summary>Maps to the app's <c>SidePane</c> (Index == the Figures pane).</summary>
public enum Pane { None, Outline, Bookmarks, Figures, Search }

/// <summary>Maps to the app's <c>AnnotationTool</c>.</summary>
public enum Tool { None, Highlight, Pen, Rectangle, TextNote, Eraser }
