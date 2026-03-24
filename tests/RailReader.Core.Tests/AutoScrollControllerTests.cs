using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AutoScrollControllerTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentState _doc;
    private readonly AutoScrollController _autoScroll;
    private readonly AppConfig _config;

    public AutoScrollControllerTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        _config = new AppConfig();
        var factory = TestFixtures.CreatePdfFactory();
        _doc = new DocumentState(_pdfPath, factory.CreatePdfService(_pdfPath),
            factory.CreatePdfTextService(), _config, new SynchronousThreadMarshaller());
        _doc.LoadPageBitmap();
        _doc.CenterPage(800, 600);
        _autoScroll = new AutoScrollController(_config);

        // Set up rail mode so auto-scroll can activate
        SetupRailMode();
    }

    public void Dispose()
    {
        _doc.Dispose();
    }

    private void SetupRailMode()
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
        _doc.AnalysisCache[_doc.CurrentPage] = analysis;
        _doc.Rail.SetAnalysis(analysis, _config.NavigableClasses);
        _doc.Camera.Zoom = _config.RailZoomThreshold + 1;
        _doc.Rail.UpdateZoom(_doc.Camera.Zoom, _doc.Camera.OffsetX, _doc.Camera.OffsetY, 800, 600);
    }

    [Fact]
    public void ToggleAutoScroll_Activates()
    {
        Assert.False(_autoScroll.AutoScrollActive);

        _autoScroll.ToggleAutoScroll(_doc);

        Assert.True(_autoScroll.AutoScrollActive);
    }

    [Fact]
    public void ToggleAutoScroll_Deactivates()
    {
        _autoScroll.ToggleAutoScroll(_doc);
        Assert.True(_autoScroll.AutoScrollActive);

        _autoScroll.ToggleAutoScroll(_doc);

        Assert.False(_autoScroll.AutoScrollActive);
    }

    [Fact]
    public void StopAutoScroll_ClearsState()
    {
        _autoScroll.ToggleAutoScroll(_doc);
        Assert.True(_autoScroll.AutoScrollActive);

        _autoScroll.StopAutoScroll(_doc);

        Assert.False(_autoScroll.AutoScrollActive);
    }

    [Fact]
    public void ToggleAutoScrollExclusive_DisablesJumpMode()
    {
        _autoScroll.JumpMode = true;
        Assert.True(_autoScroll.JumpMode);

        _autoScroll.ToggleAutoScrollExclusive(_doc);

        Assert.False(_autoScroll.JumpMode);
        Assert.True(_autoScroll.AutoScrollActive);
    }

    [Fact]
    public void ToggleJumpModeExclusive_StopsAutoScroll()
    {
        _autoScroll.ToggleAutoScroll(_doc);
        Assert.True(_autoScroll.AutoScrollActive);

        _autoScroll.ToggleJumpModeExclusive(_doc);

        Assert.False(_autoScroll.AutoScrollActive);
        Assert.True(_autoScroll.JumpMode);
    }
}
