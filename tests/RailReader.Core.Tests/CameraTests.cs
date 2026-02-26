using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

public class CameraTests
{
    [Fact]
    public void Camera_DefaultValues()
    {
        var cam = new Camera();
        Assert.Equal(1.0, cam.Zoom);
        Assert.Equal(0.0, cam.OffsetX);
        Assert.Equal(0.0, cam.OffsetY);
    }

    [Fact]
    public void Camera_ZoomClamp()
    {
        var cam = new Camera();
        cam.Zoom = Camera.ZoomMin - 1;
        // Camera.Zoom does not auto-clamp; DocumentState.ApplyZoom does
        // But verify the min/max constants are sensible
        Assert.True(Camera.ZoomMin > 0);
        Assert.True(Camera.ZoomMax > Camera.ZoomMin);
    }

    [Fact]
    public void DocumentState_ClampCamera_CentersSmallPage()
    {
        var config = new RailReader.Core.Services.AppConfig();
        var pdfPath = TestFixtures.GetTestPdfPath();
        var state = new DocumentState(pdfPath, config, new SynchronousThreadMarshaller());
        state.LoadPageBitmap();

        // Set viewport much larger than page
        double vpW = 2000, vpH = 2000;
        state.Camera.Zoom = 0.5;
        state.ClampCamera(vpW, vpH);

        // Page should be centered
        double scaledW = state.PageWidth * state.Camera.Zoom;
        double expectedX = (vpW - scaledW) / 2.0;
        Assert.Equal(expectedX, state.Camera.OffsetX, precision: 1);

        state.Dispose();
    }

    [Fact]
    public void DocumentState_CenterPage_FitsViewport()
    {
        var config = new RailReader.Core.Services.AppConfig();
        var pdfPath = TestFixtures.GetTestPdfPath();
        var state = new DocumentState(pdfPath, config, new SynchronousThreadMarshaller());
        state.LoadPageBitmap();

        state.CenterPage(800, 600);

        double scaledW = state.PageWidth * state.Camera.Zoom;
        double scaledH = state.PageHeight * state.Camera.Zoom;
        Assert.True(scaledW <= 800 + 1);
        Assert.True(scaledH <= 600 + 1);

        state.Dispose();
    }
}
