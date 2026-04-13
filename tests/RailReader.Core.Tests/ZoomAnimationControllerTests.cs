using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class ZoomAnimationControllerTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentState _doc;
    private readonly ZoomAnimationController _zoom;

    public ZoomAnimationControllerTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig();
        var factory = TestFixtures.CreatePdfFactory();
        _doc = new DocumentState(_pdfPath, factory.CreatePdfService(_pdfPath),
            factory.CreatePdfTextService(), config, new SynchronousThreadMarshaller());
        _doc.LoadPageBitmap();
        _doc.CenterPage(800, 600);
        _zoom = new ZoomAnimationController();
    }

    public void Dispose()
    {
        _doc.Dispose();
    }

    [Fact]
    public void Start_SetsIsAnimating()
    {
        Assert.False(_zoom.IsAnimating);

        _zoom.Start(_doc, 2.0, 400, 300, 800);

        Assert.True(_zoom.IsAnimating);
        Assert.Equal(2.0, _zoom.PendingTargetZoom);
    }

    [Fact]
    public void Tick_ProgressesAnimation()
    {
        _doc.Camera.Zoom = 1.0;
        _zoom.Start(_doc, 3.0, 400, 300, 800);

        // Let a tiny bit of time elapse so the animation progresses
        Thread.Sleep(10);

        bool cameraChanged = false;
        bool animating = false;
        _zoom.Tick(_doc, 800, 600, ref cameraChanged, ref animating);

        Assert.True(cameraChanged);
        // Zoom should have moved toward the target
        Assert.True(_doc.Camera.Zoom > 1.0);
    }

    [Fact]
    public void Tick_CompletesAnimation()
    {
        _doc.Camera.Zoom = 1.0;
        _zoom.Start(_doc, 2.0, 400, 300, 800);

        // Wait longer than the 180ms animation duration
        Thread.Sleep(200);

        bool cameraChanged = false;
        bool animating = false;
        _zoom.Tick(_doc, 800, 600, ref cameraChanged, ref animating);

        Assert.True(cameraChanged);
        Assert.False(animating);
        Assert.False(_zoom.IsAnimating);
        // Zoom should have reached the target
        Assert.Equal(2.0, _doc.Camera.Zoom, 2);
    }

    [Fact]
    public void Cancel_ClearsAnimation()
    {
        _zoom.Start(_doc, 2.0, 400, 300, 800);
        Assert.True(_zoom.IsAnimating);

        _zoom.Cancel();

        Assert.False(_zoom.IsAnimating);
        Assert.Null(_zoom.PendingTargetZoom);
    }

    [Fact]
    public void Start_WhileAnimating_UpdatesTarget()
    {
        _zoom.Start(_doc, 2.0, 400, 300, 800);
        Assert.Equal(2.0, _zoom.PendingTargetZoom);

        // Start a new animation to a different zoom level while the first is in progress
        _zoom.Start(_doc, 4.0, 400, 300, 800);

        Assert.True(_zoom.IsAnimating);
        Assert.Equal(4.0, _zoom.PendingTargetZoom);
    }
}
