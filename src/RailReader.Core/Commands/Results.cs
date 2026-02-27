using RailReader.Core.Models;

namespace RailReader.Core.Commands;

/// <summary>
/// Summary of a single open document.
/// </summary>
public sealed record DocumentInfo(
    string FilePath,
    string Title,
    int PageCount,
    int CurrentPage,
    double Zoom,
    double OffsetX,
    double OffsetY,
    bool RailActive,
    bool HasAnalysis,
    int NavigableBlocks,
    bool AutoScrollActive,
    bool JumpMode);

/// <summary>
/// List of all open documents with active index.
/// </summary>
public sealed record DocumentList(
    int ActiveIndex,
    List<DocumentSummary> Documents);

public sealed record DocumentSummary(
    int Index,
    string FilePath,
    string Title,
    int PageCount,
    int CurrentPage);

/// <summary>
/// Result of a navigation action.
/// </summary>
public sealed record NavigationResult(
    bool Success,
    int CurrentPage,
    string? Message = null);

/// <summary>
/// Result of a search operation.
/// </summary>
public sealed record SearchResult(
    int TotalMatches,
    int ActiveIndex,
    Dictionary<int, int> MatchesPerPage);

/// <summary>
/// Extracted text content for a page.
/// </summary>
public sealed record TextContent(
    int Page,
    string Text);

/// <summary>
/// Layout analysis information for a page.
/// </summary>
public sealed record LayoutInfo(
    int Page,
    List<BlockInfo> Blocks);

public sealed record BlockInfo(
    string ClassName,
    float X,
    float Y,
    float W,
    float H,
    float Confidence,
    int ReadingOrder,
    int LineCount,
    bool Navigable);

/// <summary>
/// Options for headless screenshot export.
/// </summary>
public sealed record ScreenshotOptions
{
    public int Dpi { get; init; } = 300;
    public bool RailOverlay { get; init; } = true;
    public bool Annotations { get; init; } = true;
    public bool SearchHighlights { get; init; } = true;
    public bool DebugOverlay { get; init; } = false;
    public bool LineFocusBlur { get; init; } = false;
    public float LineFocusBlurIntensity { get; init; } = 0.5f;
    public LineHighlightTint LineHighlightTint { get; init; } = LineHighlightTint.Auto;
    public double LineHighlightOpacity { get; init; } = 0.25;

    /// <summary>
    /// When true, crop the output to simulate what's visible in the viewport
    /// at the document's current camera position and zoom level.
    /// The output dimensions match ViewportWidth x ViewportHeight.
    /// </summary>
    public bool SimulateViewport { get; init; } = false;
    public int ViewportWidth { get; init; } = 1200;
    public int ViewportHeight { get; init; } = 900;
}

/// <summary>
/// Result of a screenshot or image export.
/// </summary>
public sealed record ExportResult(string FilePath, int Width, int Height, long FileSize);
