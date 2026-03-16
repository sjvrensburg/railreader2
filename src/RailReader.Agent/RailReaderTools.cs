using System.ComponentModel;
using RailReader.Core;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Agent;

/// <summary>
/// Tool methods exposed to the AI agent via Microsoft.Extensions.AI.
/// Each method wraps a DocumentController operation and returns structured data.
/// </summary>
public sealed class RailReaderTools
{
    private readonly DocumentController _controller;

    public RailReaderTools(DocumentController controller)
    {
        _controller = controller;
    }

    [Description("Open a PDF document. Returns document info including page count.")]
    public DocumentInfo? OpenDocument(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var state = _controller.CreateDocument(filePath);
        state.LoadPageBitmap();
        state.LoadAnnotations();
        _controller.AddDocument(state);
        return _controller.GetDocumentInfo();
    }

    [Description("List all open documents with their current state.")]
    public DocumentList ListDocuments()
    {
        return _controller.ListDocuments();
    }

    [Description("Get detailed info about the active document (or a specific one by index).")]
    public DocumentInfo? GetActiveDocument(int? index = null)
    {
        return _controller.GetDocumentInfo(index);
    }

    [Description("Navigate to a specific page (0-based index).")]
    public NavigationResult GoToPage(int page)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return new NavigationResult(false, -1, "No document open");
        if (page < 0 || page >= doc.PageCount)
            return new NavigationResult(false, doc.CurrentPage, $"Page {page} out of range (0-{doc.PageCount - 1})");

        _controller.GoToPage(page);
        return new NavigationResult(true, doc.CurrentPage);
    }

    [Description("Go to the next page.")]
    public NavigationResult NextPage()
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return new NavigationResult(false, -1, "No document open");
        if (doc.CurrentPage >= doc.PageCount - 1)
            return new NavigationResult(false, doc.CurrentPage, "Already on last page");

        _controller.GoToPage(doc.CurrentPage + 1);
        return new NavigationResult(true, doc.CurrentPage);
    }

    [Description("Go to the previous page.")]
    public NavigationResult PrevPage()
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return new NavigationResult(false, -1, "No document open");
        if (doc.CurrentPage <= 0)
            return new NavigationResult(false, doc.CurrentPage, "Already on first page");

        _controller.GoToPage(doc.CurrentPage - 1);
        return new NavigationResult(true, doc.CurrentPage);
    }

    [Description("Extract text content from a page. Returns the full text of the page.")]
    public TextContent? GetPageText(int? page = null)
    {
        return _controller.GetPageText(page);
    }

    [Description("Get layout analysis info for a page (detected blocks, reading order, etc.).")]
    public LayoutInfo? GetLayoutInfo(int? page = null)
    {
        return _controller.GetLayoutInfo(page);
    }

    [Description("Search for text across all pages. Supports regex and case sensitivity.")]
    public SearchResult Search(string query, bool caseSensitive = false, bool regex = false)
    {
        _controller.ExecuteSearch(query, caseSensitive, regex);
        return _controller.GetSearchState();
    }

    [Description("Close the active document.")]
    public bool CloseDocument()
    {
        if (_controller.ActiveDocument is null) return false;
        _controller.CloseDocument(_controller.ActiveDocumentIndex);
        return true;
    }

    [Description("Set the zoom level of the active document.")]
    public DocumentInfo? SetZoom(double level)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return null;
        var (ww, wh) = _controller.GetViewportSize();
        doc.ApplyZoom(level, ww, wh);
        return _controller.GetDocumentInfo();
    }

    [Description("Add a highlight annotation on the specified page.")]
    public bool AddHighlight(int page, float x, float y, float w, float h)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return false;

        var highlight = new RailReader.Core.Models.HighlightAnnotation
        {
            Rects = [new RailReader.Core.Models.HighlightRect(x, y, w, h)],
            Color = "#FFFF00",
            Opacity = 0.35f,
        };
        doc.AddAnnotation(page, highlight);
        return true;
    }

    [Description("Add a text note annotation at the specified position.")]
    public bool AddTextAnnotation(int page, float x, float y, string text)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return false;

        var note = new RailReader.Core.Models.TextNoteAnnotation
        {
            X = x,
            Y = y,
            Text = text,
            Color = "#FFCC00",
            Opacity = 0.9f,
        };
        doc.AddAnnotation(page, note);
        return true;
    }

    [Description("Export the active document as a PDF with annotations rendered into pages.")]
    public bool ExportPdf(string outputPath)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return false;

        try
        {
            AnnotationExportService.Export(
                doc.Pdf, doc.Annotations, outputPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Description("Export the current page as a PNG screenshot with overlays (rail, annotations, search highlights, debug).")]
    public ExportResult? ExportPageImage(
        string outputPath,
        int dpi = 150,
        bool railOverlay = true,
        bool annotations = true,
        bool searchHighlights = true,
        bool debugOverlay = false,
        bool lineFocusBlur = false)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return null;

        var options = new ScreenshotOptions
        {
            Dpi = dpi,
            RailOverlay = railOverlay,
            Annotations = annotations,
            SearchHighlights = searchHighlights,
            DebugOverlay = debugOverlay,
            LineFocusBlur = lineFocusBlur,
        };

        using var bitmap = ScreenshotCompositor.RenderPage(doc, _controller, options);
        ScreenshotCompositor.SavePng(bitmap, outputPath);

        var fileInfo = new FileInfo(outputPath);
        return new ExportResult(
            Path.GetFullPath(outputPath),
            bitmap.Width,
            bitmap.Height,
            fileInfo.Length);
    }

    [Description("Wait for layout analysis to complete on the current page. Returns layout info when ready, or null on timeout.")]
    public LayoutInfo? WaitForAnalysis(int timeoutMs = 10000)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return null;

        // If already cached, return immediately
        if (doc.AnalysisCache.ContainsKey(doc.CurrentPage))
        {
            var (ww, wh) = _controller.GetViewportSize();
            doc.UpdateRailZoom(ww, wh);
            return _controller.GetLayoutInfo();
        }

        // Poll until analysis completes or timeout
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            _controller.PollAnalysisResults();
            if (doc.AnalysisCache.ContainsKey(doc.CurrentPage))
            {
                var (ww, wh) = _controller.GetViewportSize();
                doc.UpdateRailZoom(ww, wh);
                return _controller.GetLayoutInfo();
            }
            Thread.Sleep(50);
        }

        return null;
    }

    [Description("Set the rail navigation position to a specific block and line index.")]
    public NavigationResult SetRailPosition(int block, int line = 0)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return new NavigationResult(false, -1, "No document open");
        if (!doc.Rail.Active || !doc.Rail.HasAnalysis)
            return new NavigationResult(false, doc.CurrentPage, "Rail mode not active (zoom in or wait for analysis)");
        if (block < 0 || block >= doc.Rail.NavigableCount)
            return new NavigationResult(false, doc.CurrentPage, $"Block {block} out of range (0-{doc.Rail.NavigableCount - 1})");

        doc.Rail.CurrentBlock = block;
        doc.Rail.CurrentLine = Math.Clamp(line, 0, doc.Rail.CurrentLineCount - 1);
        var (ww, wh) = _controller.GetViewportSize();
        doc.StartSnap(ww, wh);
        return new NavigationResult(true, doc.CurrentPage, $"Rail at block {block}, line {doc.Rail.CurrentLine}");
    }

    [Description("Set the colour effect filter. Effects: None, HighContrast, HighVisibility, Amber, Invert.")]
    public bool SetColourEffect(string effect, float intensity = 1.0f)
    {
        if (!Enum.TryParse<ColourEffect>(effect, ignoreCase: true, out var parsed))
            return false;

        _controller.SetColourIntensity(Math.Clamp(intensity, 0f, 1f));
        _controller.SetColourEffect(parsed);
        return true;
    }
}
