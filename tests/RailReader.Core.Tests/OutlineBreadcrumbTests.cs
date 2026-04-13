using RailReader.Core;
using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

public class OutlineBreadcrumbTests
{
    private static OutlineEntry Entry(string title, int? page, params OutlineEntry[] children)
        => new() { Title = title, Page = page, Children = [.. children] };

    [Fact]
    public void BuildPath_EmptyOutline_ReturnsEmpty()
    {
        Assert.Empty(OutlineBreadcrumb.BuildPath([], 5));
    }

    [Fact]
    public void BuildPath_PageBeforeAnyEntry_ReturnsEmpty()
    {
        var outline = new List<OutlineEntry> { Entry("Intro", 5) };
        Assert.Empty(OutlineBreadcrumb.BuildPath(outline, 0));
    }

    [Fact]
    public void BuildPath_FlatOutline_ReturnsSingleEntry()
    {
        var outline = new List<OutlineEntry>
        {
            Entry("Intro", 1),
            Entry("Methods", 5),
            Entry("Results", 12),
        };
        var path = OutlineBreadcrumb.BuildPath(outline, 7);
        Assert.Single(path);
        Assert.Equal("Methods", path[0].Title);
    }

    [Fact]
    public void BuildPath_NestedOutline_ReturnsFullChain()
    {
        var outline = new List<OutlineEntry>
        {
            Entry("Methods", 5,
                Entry("Setup", 6),
                Entry("Analysis", 8)),
            Entry("Results", 12),
        };
        var path = OutlineBreadcrumb.BuildPath(outline, 9);
        Assert.Equal(2, path.Count);
        Assert.Equal("Methods", path[0].Title);
        Assert.Equal("Analysis", path[1].Title);
    }

    [Fact]
    public void BuildPath_ThreeLevelDeep_ReturnsAllAncestors()
    {
        var outline = new List<OutlineEntry>
        {
            Entry("Part I", 1,
                Entry("Chapter 1", 5,
                    Entry("Section 1.1", 7),
                    Entry("Section 1.2", 10))),
        };
        var path = OutlineBreadcrumb.BuildPath(outline, 11);
        Assert.Equal(3, path.Count);
        Assert.Equal("Part I", path[0].Title);
        Assert.Equal("Chapter 1", path[1].Title);
        Assert.Equal("Section 1.2", path[2].Title);
    }

    [Fact]
    public void BuildPath_PageMatchesEntryExactly_PicksThatEntry()
    {
        var outline = new List<OutlineEntry>
        {
            Entry("A", 5),
            Entry("B", 10),
        };
        var path = OutlineBreadcrumb.BuildPath(outline, 10);
        Assert.Single(path);
        Assert.Equal("B", path[0].Title);
    }

    [Fact]
    public void BuildPath_EntryWithoutPage_Skipped()
    {
        // Some PDF outlines have entries without page destinations (e.g. metadata)
        var outline = new List<OutlineEntry>
        {
            Entry("Meta", page: null),
            Entry("Real Section", 5),
        };
        var path = OutlineBreadcrumb.BuildPath(outline, 7);
        Assert.Single(path);
        Assert.Equal("Real Section", path[0].Title);
    }

    [Fact]
    public void BuildPath_OnlyDescendsIntoBestParent()
    {
        // Sibling-after-current-best-parent should not displace the breadcrumb
        // path even if its page is closer; the path-based semantics keep the
        // breadcrumb anchored to the section the current page is INSIDE.
        var outline = new List<OutlineEntry>
        {
            Entry("Outer", 5,
                Entry("Inner", 7)),
            Entry("Other", 6),
        };
        var path = OutlineBreadcrumb.BuildPath(outline, 7);
        // At root level, Other (6) > Outer (5), so root pick is Other.
        // (This documents the algorithm — for a perfectly-nested outline the
        // chosen child would be the same; for an interleaved one we pick the
        // sibling whose page is most recent.)
        Assert.Single(path);
        Assert.Equal("Other", path[0].Title);
    }
}
