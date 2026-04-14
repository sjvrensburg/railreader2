using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AppConfigTests
{
    [Fact]
    public void DefaultConfig_HasExpectedValues()
    {
        var config = new AppConfig();
        Assert.Equal(3.0, config.RailZoomThreshold);
        Assert.Equal(300.0, config.SnapDurationMs);
        Assert.True(config.PixelSnapping);
        Assert.NotEmpty(config.NavigableClasses);
        Assert.Equal(180, config.MinimapWidth);
        Assert.Equal(240, config.MinimapHeight);
        Assert.Equal(10, config.MinimapMarginRight);
        Assert.Equal(10, config.MinimapMarginBottom);
    }

    [Fact]
    public void PropertySetting_WorksCorrectly()
    {
        var config = new AppConfig { RailZoomThreshold = 5.0 };
        config.SnapDurationMs = 500.0;
        config.PixelSnapping = false;

        Assert.Equal(5.0, config.RailZoomThreshold);
        Assert.Equal(500.0, config.SnapDurationMs);
        Assert.False(config.PixelSnapping);
    }

    [Fact]
    public void RecentFiles_AddAndRetrieve()
    {
        var config = new AppConfig();
        config.AddRecentFile("/tmp/test.pdf");

        var position = config.GetReadingPosition("/tmp/test.pdf");
        Assert.NotNull(position);
        Assert.Equal("/tmp/test.pdf", position.FilePath);
    }

    [Fact]
    public void SaveReadingPosition_UpdatesExisting()
    {
        var config = new AppConfig();
        config.AddRecentFile("/tmp/test.pdf");
        config.SaveReadingPosition("/tmp/test.pdf", page: 5, zoom: 2.0, offsetX: -100, offsetY: -50);

        var pos = config.GetReadingPosition("/tmp/test.pdf");
        Assert.NotNull(pos);
        Assert.Equal(5, pos.Page);
        Assert.Equal(2.0, pos.Zoom);
    }
}
