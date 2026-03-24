using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class DocumentControllerTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;

    public DocumentControllerTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig();
        _controller = new DocumentController(config, new SynchronousThreadMarshaller(),
            TestFixtures.CreatePdfFactory());
        // Don't initialize worker (requires ONNX model) — test navigation without analysis
    }

    public void Dispose()
    {
        foreach (var doc in _controller.Documents.ToList())
            doc.Dispose();
    }

    [Fact]
    public void CreateDocument_ReturnsValidState()
    {
        var state = _controller.CreateDocument(_pdfPath);
        Assert.Equal(3, state.PageCount);
        Assert.Equal(0, state.CurrentPage);
        Assert.NotEmpty(state.Title);
        state.Dispose();
    }

    [Fact]
    public void AddDocument_SetsActiveIndex()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        Assert.Single(_controller.Documents);
        Assert.Equal(0, _controller.ActiveDocumentIndex);
        Assert.Same(state, _controller.ActiveDocument);
    }

    [Fact]
    public void CloseDocument_RemovesFromList()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        _controller.CloseDocument(0);
        Assert.Empty(_controller.Documents);
    }

    [Fact]
    public void ListDocuments_ReturnsCorrectInfo()
    {
        var s1 = _controller.CreateDocument(_pdfPath);
        s1.LoadPageBitmap();
        _controller.AddDocument(s1);

        var list = _controller.ListDocuments();
        Assert.Equal(0, list.ActiveIndex);
        Assert.Single(list.Documents);
        Assert.Equal(3, list.Documents[0].PageCount);
    }

    [Fact]
    public void GetDocumentInfo_ReturnsCorrectState()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        var info = _controller.GetDocumentInfo();
        Assert.NotNull(info);
        Assert.Equal(_pdfPath, info.FilePath);
        Assert.Equal(3, info.PageCount);
        Assert.Equal(0, info.CurrentPage);
    }

    [Fact]
    public void FitPage_SetsZoom()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        _controller.FitPage();
        Assert.True(state.Camera.Zoom > 0);
    }

    [Fact]
    public void FitWidth_SetsZoom()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        _controller.FitWidth();
        Assert.True(state.Camera.Zoom > 0);
    }

    [Fact]
    public void MoveDocument_ReordersCorrectly()
    {
        var s1 = _controller.CreateDocument(_pdfPath);
        s1.LoadPageBitmap();
        _controller.AddDocument(s1);

        var s2 = _controller.CreateDocument(_pdfPath);
        s2.LoadPageBitmap();
        _controller.AddDocument(s2);

        _controller.ActiveDocumentIndex = 0;
        _controller.MoveDocument(0, 1);
        Assert.Same(s1, _controller.Documents[1]);
        Assert.Same(s2, _controller.Documents[0]);
    }

    // --- Helper for rail mode tests ---

    private void SetupRailMode(DocumentState doc)
    {
        var analysis = new PageAnalysis();
        var block = new LayoutBlock
        {
            ClassId = 22, BBox = new BBox(72, 72, 468, 200),
            Confidence = 0.9f, Order = 0,
        };
        for (int i = 0; i < 5; i++)
            block.Lines.Add(new LineInfo(72 + i * 20, 16));
        analysis.Blocks.Add(block);
        doc.AnalysisCache[doc.CurrentPage] = analysis;
        doc.Rail.SetAnalysis(analysis, _controller.Config.NavigableClasses);
        doc.Camera.Zoom = _controller.Config.RailZoomThreshold + 1;
        var (ww, wh) = _controller.GetViewportSize();
        doc.Rail.UpdateZoom(doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, ww, wh);
    }

    // --- New tests ---

    [Fact]
    public void Tick_NoDocument_ReturnsDefault()
    {
        var result = _controller.Tick(0.016);
        Assert.False(result.CameraChanged);
        Assert.False(result.PageChanged);
        Assert.False(result.OverlayChanged);
        Assert.False(result.SearchChanged);
        Assert.False(result.AnnotationsChanged);
        Assert.False(result.StillAnimating);
    }

    [Fact]
    public void GoToPage_UpdatesCurrentPage()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        _controller.GoToPage(1);
        Assert.Equal(1, _controller.ActiveDocument!.CurrentPage);
    }

    [Fact]
    public void ToggleAutoScroll_ActivatesAndDeactivates()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);

        _controller.ToggleAutoScroll();
        Assert.False(_controller.AutoScrollActive);
    }

    [Fact]
    public void ToggleJumpModeExclusive_StopsAutoScroll()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);

        _controller.ToggleJumpModeExclusive();
        Assert.False(_controller.AutoScrollActive);
        Assert.True(_controller.JumpMode);
    }

    [Fact]
    public void CycleColourEffect_Cycles()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        var next = _controller.CycleColourEffect();
        Assert.NotEqual(ColourEffect.None, next);
    }

    [Fact]
    public void HandleVerticalNav_AdvancesLine()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        int lineBefore = state.Rail.CurrentLine;
        _controller.HandleArrowDown();
        Assert.Equal(lineBefore + 1, state.Rail.CurrentLine);
    }

    [Fact]
    public void Tick_WithDocument_DoesNotCrash()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 10; i++)
                _controller.Tick(0.016);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void FitPage_UpdatesZoom()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        double zoomBefore = state.Camera.Zoom;
        _controller.FitPage();
        Assert.True(state.Camera.Zoom > 0);
        Assert.NotEqual(zoomBefore, state.Camera.Zoom);
    }
}
