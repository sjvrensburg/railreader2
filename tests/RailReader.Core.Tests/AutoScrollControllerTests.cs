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

    private void SetupRailMode() => TestFixtures.SetupRailMode(_doc, _config);

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
