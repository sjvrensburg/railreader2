using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader2.RenderHarness.Headless;

/// <summary>Shared output settings plus the list of screenshots to generate.</summary>
public sealed class HeadlessConfig
{
    /// <summary>Output directory, relative to repo root.</summary>
    public string OutputDir { get; set; } = "docs/img";

    /// <summary>Default window/canvas size in device-independent pixels (per-shot overridable).</summary>
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 960;

    /// <summary>Default theme, "dark" or "light" (per-shot overridable).</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Global UI font scale (chrome size). Lower = more compact chrome relative to
    /// the page. Applied once at startup. 0 keeps the app default.</summary>
    public float UiScale { get; set; } = 0.85f;

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
    /// <summary>Output filename (no extension), e.g. "rail_mode".</summary>
    public string Name { get; set; } = "screenshot";

    /// <summary>Source PDF path, relative to repo root.</summary>
    public string Pdf { get; set; } = "";

    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Absolute camera zoom (1.0 = 100%). 0 or omitted = fit page. A value above the
    /// app's rail-zoom threshold (≈3.0) engages rail mode once the page is analysed.
    /// </summary>
    public float Zoom { get; set; } = 0f;

    /// <summary>Per-shot theme override ("dark"/"light"). Null = config default.</summary>
    public string? Theme { get; set; }

    /// <summary>Per-shot UI font scale override (chrome size). 0 = config default.
    /// Changing this rebuilds the window (the scale is read at VM construction).</summary>
    public float UiScale { get; set; } = 0f;

    /// <summary>Per-shot window size override. 0 = config default.</summary>
    public int Width { get; set; } = 0;
    public int Height { get; set; } = 0;

    /// <summary>Which accordion side pane is open (none hides the panel).</summary>
    public Pane Sidebar { get; set; } = Pane.None;

    /// <summary>Which annotation tool is active.</summary>
    public Tool Tool { get; set; } = Tool.None;

    /// <summary>Colour-effect filter applied to the PDF content.</summary>
    public ColourFx ColourEffect { get; set; } = ColourFx.None;

    /// <summary>Show the layout-analysis debug overlay (detected blocks + reading order).</summary>
    public bool DebugOverlay { get; set; } = false;

    /// <summary>Rail mode: tint the active line.</summary>
    public bool LineHighlight { get; set; } = false;

    /// <summary>Rail mode: blur/dim the non-active lines.</summary>
    public bool LineFocusBlur { get; set; } = false;

    /// <summary>Rail mode: place the active line this fraction down from the top of the
    /// viewport (e.g. 0.33). 0 = leave the rail's default (centred) position.</summary>
    public float RailLineFraction { get; set; } = 0f;

    /// <summary>Rail mode: advance this many lines after engaging (e.g. to move off the
    /// section heading and onto a body line).</summary>
    public int RailAdvanceLines { get; set; } = 0;

    /// <summary>Enter annotation mode so the annotation tool controls are visible.</summary>
    public bool AnnotationMode { get; set; } = false;

    /// <summary>Run this search query and open the Search pane (drives the on-page highlights).</summary>
    public string? Search { get; set; }

    /// <summary>Inject a demonstration set of annotations (highlight, underline, freehand)
    /// placed over real detected text on this page.</summary>
    public bool Annotate { get; set; } = false;

    /// <summary>Wait for ONNX layout analysis of this page before capturing (needed for rail
    /// mode, the Figures pane, the debug overlay, and annotation placement).</summary>
    public bool RequireAnalysis { get; set; } = false;
}

/// <summary>Maps to the app's <c>SidePane</c> (Figures == the Index pane).</summary>
public enum Pane { None, Outline, Bookmarks, Figures, Search }

/// <summary>Maps to the app's <c>AnnotationTool</c>.</summary>
public enum Tool { None, Highlight, Pen, Rectangle, TextNote, Eraser }

/// <summary>Maps to the app's <c>ColourEffect</c>.</summary>
public enum ColourFx { None, HighContrast, HighVisibility, Amber, Invert }
