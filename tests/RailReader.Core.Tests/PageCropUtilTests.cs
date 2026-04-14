using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class PageCropUtilTests
{
    [Fact]
    public void EmptyAnalysis_ReturnsFullFraction()
    {
        var analysis = new PageAnalysis { PageWidth = 600, PageHeight = 800 };
        var frac = PageCropUtil.ComputeFraction(analysis);
        Assert.Equal(ContentFraction.Full, frac);
    }

    [Fact]
    public void ZeroDimensions_ReturnsFullFraction()
    {
        var analysis = new PageAnalysis
        {
            PageWidth = 0,
            PageHeight = 0,
            Blocks = [new LayoutBlock { BBox = new BBox(10, 10, 50, 50) }],
        };
        Assert.Equal(ContentFraction.Full, PageCropUtil.ComputeFraction(analysis));
    }

    [Fact]
    public void SingleCentredBlock_ReturnsCorrectFraction()
    {
        var analysis = new PageAnalysis
        {
            PageWidth = 1000,
            PageHeight = 1000,
            Blocks = [new LayoutBlock { BBox = new BBox(100, 200, 800, 600) }],
        };
        var frac = PageCropUtil.ComputeFraction(analysis);
        Assert.Equal(0.1, frac.X, 6);
        Assert.Equal(0.2, frac.Y, 6);
        Assert.Equal(0.8, frac.W, 6);
        Assert.Equal(0.6, frac.H, 6);
    }

    [Fact]
    public void MultipleBlocks_UnionIsBoundingBox()
    {
        var analysis = new PageAnalysis
        {
            PageWidth = 1000,
            PageHeight = 1000,
            Blocks =
            [
                new LayoutBlock { BBox = new BBox(100, 100, 200, 200) }, // top-left quarter
                new LayoutBlock { BBox = new BBox(700, 700, 200, 200) }, // bottom-right quarter
            ],
        };
        var frac = PageCropUtil.ComputeFraction(analysis);
        Assert.Equal(0.1, frac.X, 6);
        Assert.Equal(0.1, frac.Y, 6);
        Assert.Equal(0.8, frac.W, 6);
        Assert.Equal(0.8, frac.H, 6);
    }

    [Fact]
    public void Union_OfTwoFractions_ExpandsOutward()
    {
        var a = new ContentFraction(0.10, 0.15, 0.80, 0.70);
        var b = new ContentFraction(0.08, 0.20, 0.85, 0.60);
        var u = a.Union(b);
        Assert.Equal(0.08, u.X, 6);
        Assert.Equal(0.15, u.Y, 6);
        Assert.Equal(0.93 - 0.08, u.W, 6);  // max right 0.93
        Assert.Equal(0.85 - 0.15, u.H, 6);  // max bottom 0.85
    }
}
