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
}
