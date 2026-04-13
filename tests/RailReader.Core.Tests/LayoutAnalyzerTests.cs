using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class LayoutAnalyzerTests
{
    private static LayoutBlock Block(float x, float y, float w, float h, float conf = 0.9f, int classId = 22, int order = 0)
        => new() { BBox = new BBox(x, y, w, h), Confidence = conf, ClassId = classId, Order = order };

    [Fact]
    public void Iou_IdenticalBoxes_Returns1()
    {
        var a = new BBox(0, 0, 10, 10);
        var b = new BBox(0, 0, 10, 10);
        Assert.Equal(1f, LayoutAnalyzer.Iou(a, b), 4);
    }

    [Fact]
    public void Iou_DisjointBoxes_Returns0()
    {
        var a = new BBox(0, 0, 10, 10);
        var b = new BBox(20, 20, 10, 10);
        Assert.Equal(0f, LayoutAnalyzer.Iou(a, b));
    }

    [Fact]
    public void Iou_HalfOverlap_ReturnsOneThird()
    {
        var a = new BBox(0, 0, 10, 10);
        var b = new BBox(5, 0, 10, 10);
        // intersection 50, union 150 → 1/3
        Assert.Equal(1f / 3f, LayoutAnalyzer.Iou(a, b), 4);
    }

    [Fact]
    public void Iou_ZeroAreaBoxes_Returns0()
    {
        var a = new BBox(0, 0, 0, 0);
        var b = new BBox(0, 0, 0, 0);
        Assert.Equal(0f, LayoutAnalyzer.Iou(a, b));
    }

    [Fact]
    public void Nms_RemovesOverlappingLowerConfidenceBlock()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 10, 10, conf: 0.5f),
            Block(1, 1, 10, 10, conf: 0.9f), // highest conf wins
        };
        LayoutAnalyzer.Nms(blocks, threshold: 0.3f);
        Assert.Single(blocks);
        Assert.Equal(0.9f, blocks[0].Confidence);
    }

    [Fact]
    public void Nms_KeepsNonOverlappingBlocks()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 10, 10, conf: 0.9f),
            Block(50, 50, 10, 10, conf: 0.8f),
        };
        LayoutAnalyzer.Nms(blocks, threshold: 0.3f);
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Nms_SortsByConfidenceDescending()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 10, 10, conf: 0.3f),
            Block(100, 100, 10, 10, conf: 0.9f),
            Block(200, 200, 10, 10, conf: 0.6f),
        };
        LayoutAnalyzer.Nms(blocks, threshold: 0.5f);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(0.9f, blocks[0].Confidence);
        Assert.Equal(0.6f, blocks[1].Confidence);
        Assert.Equal(0.3f, blocks[2].Confidence);
    }

    [Fact]
    public void SuppressNestedBlocks_RemovesInnerBlock()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 100),      // outer
            Block(10, 10, 20, 20),      // fully contained
        };
        LayoutAnalyzer.SuppressNestedBlocks(blocks);
        Assert.Single(blocks);
        Assert.Equal(100f, blocks[0].BBox.W);
    }

    [Fact]
    public void SuppressNestedBlocks_KeepsPartiallyOverlappingBlocks()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 50, 50),
            Block(40, 40, 50, 50), // overlaps but not contained
        };
        LayoutAnalyzer.SuppressNestedBlocks(blocks);
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void SuppressNestedBlocks_InnerBarelyOutsideMargin_TreatsAsContained()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 100),
            Block(-1, -1, 50, 50), // inner 1pt outside on top-left, within 2pt tolerance
        };
        LayoutAnalyzer.SuppressNestedBlocks(blocks);
        Assert.Single(blocks);
        Assert.Equal(100f, blocks[0].BBox.W);
    }

    [Fact]
    public void SuppressNestedBlocks_InnerOutsideMargin_KeepsBoth()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 100),
            Block(-5, -5, 50, 50), // 5pt outside on top-left, exceeds 2pt tolerance
        };
        LayoutAnalyzer.SuppressNestedBlocks(blocks);
        Assert.Equal(2, blocks.Count);
    }
}
