using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class ColorUtilsTests
{
    [Fact]
    public void ParseHexColor_ValidHex_ReturnsCorrectColor()
    {
        var color = ColorUtils.ParseHexColor("#FF8000", 200);

        Assert.Equal(255, color.R);
        Assert.Equal(128, color.G);
        Assert.Equal(0, color.B);
        Assert.Equal(200, color.A);
    }

    [Fact]
    public void ParseHexColor_Black_ReturnsBlack()
    {
        var color = ColorUtils.ParseHexColor("#000000", 255);

        Assert.Equal(0, color.R);
        Assert.Equal(0, color.G);
        Assert.Equal(0, color.B);
        Assert.Equal(255, color.A);
    }

    [Fact]
    public void ParseHexColor_White_ReturnsWhite()
    {
        var color = ColorUtils.ParseHexColor("#FFFFFF", 128);

        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(255, color.B);
        Assert.Equal(128, color.A);
    }

    [Fact]
    public void ParseHexColor_InvalidFormat_ReturnsFallbackYellow()
    {
        var color = ColorUtils.ParseHexColor("not-a-color", 100);

        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
        Assert.Equal(100, color.A);
    }

    [Fact]
    public void ParseHexColor_TooShort_ReturnsFallbackYellow()
    {
        var color = ColorUtils.ParseHexColor("#FFF", 50);

        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
        Assert.Equal(50, color.A);
    }

    [Fact]
    public void ParseHexColor_NoHash_ReturnsFallbackYellow()
    {
        var color = ColorUtils.ParseHexColor("FF8000", 255);

        // Missing # prefix — 6 chars, not 7 with #
        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public void ParseHexColor_LowercaseHex_Works()
    {
        var color = ColorUtils.ParseHexColor("#ff0080", 255);

        Assert.Equal(255, color.R);
        Assert.Equal(0, color.G);
        Assert.Equal(128, color.B);
    }
}
