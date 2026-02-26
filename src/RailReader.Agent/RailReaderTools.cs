using System.ComponentModel;
using RailReader.Core;
using RailReader.Core.Commands;

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
        if (doc is null || doc.Annotations is null) return false;

        try
        {
            RailReader.Core.Services.AnnotationExportService.Export(
                doc.Pdf, doc.Annotations, outputPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
