using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class MarginCroppingTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;

    public MarginCroppingTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        _controller = new DocumentController(new AppConfig(),
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose()
    {
        foreach (var doc in _controller.Documents.ToList())
            doc.Dispose();
    }

    [Fact]
    public void SetAnalysis_AccumulatesDocumentContentFraction()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);

        Assert.Null(state.DocumentContentFraction);

        state.SetAnalysis(0, new PageAnalysis
        {
            PageWidth = 1000,
            PageHeight = 1000,
            Blocks = [new LayoutBlock { BBox = new BBox(100, 100, 800, 800) }],
        });

        var frac = state.DocumentContentFraction;
        Assert.NotNull(frac);
        Assert.Equal(0.1, frac!.Value.X, 6);
        Assert.Equal(0.8, frac.Value.W, 6);

        state.SetAnalysis(1, new PageAnalysis
        {
            PageWidth = 1000,
            PageHeight = 1000,
            Blocks = [new LayoutBlock { BBox = new BBox(50, 100, 900, 800) }],
        });

        frac = state.DocumentContentFraction;
        Assert.Equal(0.05, frac!.Value.X, 6);
        Assert.Equal(0.95 - 0.05, frac.Value.W, 6);
    }

    [Fact]
    public void FitWidth_WithCroppingOff_UsesFullPageWidth()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        state.SetAnalysis(0, new PageAnalysis
        {
            PageWidth = state.PageWidth,
            PageHeight = state.PageHeight,
            Blocks = [new LayoutBlock
            {
                BBox = new BBox(
                    (float)(state.PageWidth * 0.1),
                    (float)(state.PageHeight * 0.1),
                    (float)(state.PageWidth * 0.8),
                    (float)(state.PageHeight * 0.8))
            }],
        });

        state.MarginCropping = false;
        state.FitWidth(800, 600);
        double uncroppedZoom = state.Camera.Zoom;

        double expected = 800.0 / state.PageWidth;
        Assert.Equal(expected, uncroppedZoom, 3);
    }

    [Fact]
    public void FitWidth_WithCroppingOn_ZoomsTighterToContent()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        state.SetAnalysis(0, new PageAnalysis
        {
            PageWidth = state.PageWidth,
            PageHeight = state.PageHeight,
            Blocks = [new LayoutBlock
            {
                BBox = new BBox(
                    (float)(state.PageWidth * 0.1),
                    (float)(state.PageHeight * 0.1),
                    (float)(state.PageWidth * 0.8),
                    (float)(state.PageHeight * 0.8))
            }],
        });

        state.MarginCropping = false;
        state.FitWidth(800, 600);
        double uncroppedZoom = state.Camera.Zoom;

        state.MarginCropping = true;
        state.FitWidth(800, 600);
        double croppedZoom = state.Camera.Zoom;

        Assert.True(croppedZoom > uncroppedZoom,
            $"Cropped zoom {croppedZoom} should exceed uncropped {uncroppedZoom}");
        // Content is 80% of page width → zoom should be 1/0.8 = 1.25× the uncropped value
        Assert.Equal(uncroppedZoom / 0.8, croppedZoom, 3);
    }

    [Fact]
    public void FitWidth_WithCroppingOnButNoAnalysis_FallsBackToFullPage()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        state.MarginCropping = true; // enabled but no analysis yet
        state.FitWidth(800, 600);

        double expected = 800.0 / state.PageWidth;
        Assert.Equal(expected, state.Camera.Zoom, 3);
    }
}
