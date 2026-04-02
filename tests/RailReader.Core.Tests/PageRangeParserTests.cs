using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class PageRangeParserTests
{
    [Fact]
    public void Parse_Null_ReturnsAllPages()
    {
        var (pages, error) = PageRangeParser.Parse(null, 5);
        Assert.Null(error);
        Assert.Equal(new List<int> { 0, 1, 2, 3, 4 }, pages);
    }

    [Fact]
    public void Parse_Whitespace_ReturnsAllPages()
    {
        var (pages, error) = PageRangeParser.Parse("  ", 3);
        Assert.Null(error);
        Assert.Equal(new List<int> { 0, 1, 2 }, pages);
    }

    [Fact]
    public void Parse_SinglePage_ReturnsZeroBased()
    {
        var (pages, error) = PageRangeParser.Parse("3", 10);
        Assert.Null(error);
        Assert.Equal(new List<int> { 2 }, pages);
    }

    [Fact]
    public void Parse_Range_ReturnsInclusiveZeroBased()
    {
        var (pages, error) = PageRangeParser.Parse("2-4", 10);
        Assert.Null(error);
        Assert.Equal(new List<int> { 1, 2, 3 }, pages);
    }

    [Fact]
    public void Parse_CommaSeparated_ReturnsSorted()
    {
        var (pages, error) = PageRangeParser.Parse("5,1,3", 10);
        Assert.Null(error);
        Assert.Equal(new List<int> { 0, 2, 4 }, pages);
    }

    [Fact]
    public void Parse_MixedRangesAndPages()
    {
        var (pages, error) = PageRangeParser.Parse("1,3-5,8", 10);
        Assert.Null(error);
        Assert.Equal(new List<int> { 0, 2, 3, 4, 7 }, pages);
    }

    [Fact]
    public void Parse_Duplicates_Deduplicated()
    {
        var (pages, error) = PageRangeParser.Parse("1,1,2", 10);
        Assert.Null(error);
        Assert.Equal(new List<int> { 0, 1 }, pages);
    }

    [Fact]
    public void Parse_OutOfRange_ReturnsError()
    {
        var (pages, error) = PageRangeParser.Parse("11", 10);
        Assert.Null(pages);
        Assert.NotNull(error);
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void Parse_ZeroPage_ReturnsError()
    {
        var (pages, error) = PageRangeParser.Parse("0", 10);
        Assert.Null(pages);
        Assert.NotNull(error);
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void Parse_InvalidFormat_ReturnsError()
    {
        var (pages, error) = PageRangeParser.Parse("abc", 10);
        Assert.Null(pages);
        Assert.NotNull(error);
        Assert.Contains("Invalid", error);
    }

    [Fact]
    public void Parse_ReversedRange_ReturnsError()
    {
        var (pages, error) = PageRangeParser.Parse("10-5", 15);
        Assert.Null(pages);
        Assert.NotNull(error);
        Assert.Contains("start > end", error);
    }
}
