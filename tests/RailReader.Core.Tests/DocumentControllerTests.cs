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

    private void SetupRailMode(DocumentState doc)
    {
        var (ww, wh) = _controller.GetViewportSize();
        TestFixtures.SetupRailMode(doc, _controller.Config, ww, wh);
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

    // --- Pause/Resume Cycle ---

    [Fact]
    public void HandlePan_CapturesRailState()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        int expectedBlock = state.Rail.CurrentBlock;
        int expectedLine = state.Rail.CurrentLine;
        double expectedBias = state.Rail.VerticalBias;
        double expectedZoom = state.Camera.Zoom;

        // Pause: Ctrl+drag
        _controller.HandlePan(10, 10, ctrlHeld: true);
        Assert.True(_controller.RailPaused);

        // Pan some more while paused (should not change rail state)
        _controller.HandlePan(20, 20);

        // Resume
        _controller.ResumeRailFromPause();
        Assert.False(_controller.RailPaused);
        Assert.Equal(expectedBlock, state.Rail.CurrentBlock);
        Assert.Equal(expectedLine, state.Rail.CurrentLine);
        Assert.Equal(expectedBias, state.Rail.VerticalBias);
        Assert.Equal(expectedZoom, state.Camera.Zoom);
    }

    [Fact]
    public void CloseDocument_ClearsRailPause()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        _controller.HandlePan(10, 10, ctrlHeld: true);
        Assert.True(_controller.RailPaused);

        _controller.CloseDocument(0);
        Assert.False(_controller.RailPaused);
    }

    [Fact]
    public void SelectDocument_ClearsRailPause()
    {
        var s1 = _controller.CreateDocument(_pdfPath);
        s1.LoadPageBitmap();
        _controller.AddDocument(s1);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(s1);

        var s2 = _controller.CreateDocument(_pdfPath);
        s2.LoadPageBitmap();
        _controller.AddDocument(s2);

        // Pause on tab 1
        _controller.ActiveDocumentIndex = 0;
        _controller.HandlePan(10, 10, ctrlHeld: true);
        Assert.True(_controller.RailPaused);

        // Switch to tab 2
        _controller.SelectDocument(1);
        Assert.False(_controller.RailPaused);
    }

    [Fact]
    public void ResumeRailFromPause_WhenNotPaused_IsNoOp()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        double zoomBefore = state.Camera.Zoom;
        _controller.ResumeRailFromPause();
        Assert.False(_controller.RailPaused);
        Assert.Equal(zoomBefore, state.Camera.Zoom);
    }

    // --- Phase 3b: Non-Rail Edge-Hold Page Advance ---

    [Fact]
    public void HandleArrowDown_AtPageEdge_AdvancesAfterHold()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Pan camera to the top edge so it can't pan further up
        state.Camera.OffsetY = 0;
        state.ClampCamera(800, 600);

        // Pan as far up as possible to reach the edge
        for (int i = 0; i < 20; i++)
            _controller.HandleArrowUp();
        _controller.ClearPageEdgeHold();

        // Now hold ArrowDown to reach bottom edge, then edge-hold advance
        // First, pan down many times to reach the bottom edge
        for (int i = 0; i < 100; i++)
            _controller.HandleArrowDown();

        // At this point the camera should be at the bottom edge.
        // Hold for edge-advance threshold (400ms)
        Thread.Sleep(450);
        _controller.HandleArrowDown();

        Assert.True(state.CurrentPage > 0 || _controller.RailPaused);
    }

    [Fact]
    public void ClearPageEdgeHold_WhenNotHolding_IsNoOp()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Clear should be a no-op when not edge-holding
        _controller.ClearPageEdgeHold();
        Assert.Equal(0, state.CurrentPage);
    }

    // --- Phase 3c: Navigation History ---

    [Fact]
    public void NavigateToBookmark_PushesToBackStack()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Add a bookmark pointing to page 2
        state.Annotations.Bookmarks.Add(new BookmarkEntry { Name = "Test", Page = 2 });
        Assert.Equal(0, state.CurrentPage);

        _controller.NavigateToBookmark(0);
        Assert.Equal(2, state.CurrentPage);
        Assert.Equal(1, state.BackStackCount);
        Assert.Equal(0, state.PeekBack());
    }

    [Fact]
    public void NavigateBack_RestoresPage()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Set up history via bookmark navigation
        state.Annotations.Bookmarks.Add(new BookmarkEntry { Name = "Page2", Page = 2 });
        _controller.NavigateToBookmark(0);
        Assert.Equal(2, state.CurrentPage);

        _controller.NavigateBack();
        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(0, state.BackStackCount);
        Assert.Equal(1, state.ForwardStackCount);
        Assert.Equal(2, state.PeekForward());
    }

    [Fact]
    public void NavigateForward_AfterBack()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        state.Annotations.Bookmarks.Add(new BookmarkEntry { Name = "Page2", Page = 2 });
        _controller.NavigateToBookmark(0);
        _controller.NavigateBack();
        Assert.Equal(0, state.CurrentPage);

        _controller.NavigateForward();
        Assert.Equal(2, state.CurrentPage);
    }

    [Fact]
    public void NavigateBack_EmptyStack_DoesNothing()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        int pageBefore = state.CurrentPage;
        _controller.NavigateBack();
        Assert.Equal(pageBefore, state.CurrentPage);
    }

    // --- Phase 3d: Click Handling ---

    [Fact]
    public void HandleClick_LinkNavigatesAndPushesHistory()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        // Inject a link at page-point (100, 100) → page 2
        state.SetLinks(0,
        [
            new PdfLink
            {
                Rect = new RectF(50, 50, 200, 200),
                Destination = new PageDestination { PageIndex = 2 }
            }
        ]);

        // Convert page coords to canvas coords
        double canvasX = 100 * state.Camera.Zoom + state.Camera.OffsetX;
        double canvasY = 100 * state.Camera.Zoom + state.Camera.OffsetY;

        var (handled, dest) = _controller.HandleClick(canvasX, canvasY);
        Assert.True(handled);
        Assert.NotNull(dest);
        Assert.IsType<PageDestination>(dest);
        Assert.Equal(2, state.CurrentPage);
        Assert.Equal(1, state.BackStackCount);
        Assert.Equal(0, state.PeekBack());
    }

    [Fact]
    public void HandleClick_BlockSelectionInRailMode()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        SetupRailMode(state);

        // Click on a point within the rail block area
        // TestFixtures.SetupRailMode creates a block at the first navigable index
        var block = state.Rail.CurrentNavigableBlock;
        double pageX = block.BBox.X + block.BBox.W / 2;
        double pageY = block.BBox.Y + block.BBox.H / 2;
        double canvasX = pageX * state.Camera.Zoom + state.Camera.OffsetX;
        double canvasY = pageY * state.Camera.Zoom + state.Camera.OffsetY;

        var (handled, _) = _controller.HandleClick(canvasX, canvasY);
        Assert.True(handled);
    }

    [Fact]
    public void HandleClick_EmptyArea_NotHandled()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        // No rail mode, no links — click should not be handled

        var (handled, _) = _controller.HandleClick(500, 500);
        Assert.False(handled);
    }
}
