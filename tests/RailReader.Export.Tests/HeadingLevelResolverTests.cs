using Xunit;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;

namespace RailReader.Export.Tests;

public class HeadingLevelResolverTests
{
    [Fact]
    public void DocTitle_WithNoOutline_ReturnsH1()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDocTitle, 0),
        };
        var pageText = new PageText("Introduction", []);

        var result = HeadingLevelResolver.Resolve(blocks, pageText, [], 0);

        Assert.Single(result);
        Assert.Equal(1, result[0]);
    }

    [Fact]
    public void ParagraphTitle_WithNoOutline_ReturnsH2()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassParagraphTitle, 0),
        };

        var result = HeadingLevelResolver.Resolve(blocks, null, [], 0);

        Assert.Single(result);
        Assert.Equal(2, result[0]);
    }

    [Fact]
    public void MatchesOutlineDepth_ForDocTitle()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDocTitle, 0, 10, 10, 200, 30),
        };
        var pageText = MakePageText("Chapter 3: Methods", 10, 10, 200, 30);

        var outline = new List<OutlineEntry>
        {
            new()
            {
                Title = "Chapter 3: Methods",
                Page = 0,
                Children =
                [
                    new() { Title = "3.1 Data Collection", Page = 0, Children = [] },
                    new() { Title = "3.2 Analysis", Page = 1, Children = [] },
                ],
            },
        };

        var result = HeadingLevelResolver.Resolve(blocks, pageText, outline, 0);

        Assert.Equal(1, result[0]); // depth 1 in tree
    }

    [Fact]
    public void MatchesOutlineDepth_ForSubsection()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassParagraphTitle, 0, 10, 10, 200, 30),
        };
        var pageText = MakePageText("3.1 Data Collection", 10, 10, 200, 30);

        var outline = new List<OutlineEntry>
        {
            new()
            {
                Title = "Chapter 3",
                Page = 0,
                Children =
                [
                    new() { Title = "3.1 Data Collection", Page = 0, Children = [] },
                ],
            },
        };

        var result = HeadingLevelResolver.Resolve(blocks, pageText, outline, 0);

        Assert.Equal(2, result[0]); // depth 2 in tree
    }

    [Fact]
    public void NonHeadingBlocks_AreIgnored()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(22, 0), // text
            MakeBlock(LayoutConstants.ClassDocTitle, 1),
            MakeBlock(21, 2), // table
        };

        var result = HeadingLevelResolver.Resolve(blocks, null, [], 0);

        Assert.Single(result);
        Assert.True(result.ContainsKey(1));
    }

    [Fact]
    public void DeepOutline_ClampsToH6()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassParagraphTitle, 0, 10, 10, 200, 30),
        };
        var pageText = MakePageText("Deep Section", 10, 10, 200, 30);

        // Build outline 8 levels deep
        var innermost = new OutlineEntry { Title = "Deep Section", Page = 0, Children = [] };
        var current = innermost;
        for (int i = 0; i < 7; i++)
        {
            current = new OutlineEntry
            {
                Title = $"Level {7 - i}",
                Page = 0,
                Children = [current],
            };
        }

        var result = HeadingLevelResolver.Resolve(blocks, pageText, [current], 0);

        Assert.Equal(6, result[0]); // clamped to 6
    }

    [Fact]
    public void FuzzyMatch_HandlesMinorDifferences()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassParagraphTitle, 0, 10, 10, 200, 30),
        };
        // Slight OCR-style difference
        var pageText = MakePageText("3.1 Data Collectio", 10, 10, 200, 30);

        var outline = new List<OutlineEntry>
        {
            new()
            {
                Title = "Main",
                Page = 0,
                Children =
                [
                    new() { Title = "3.1 Data Collection", Page = 0, Children = [] },
                ],
            },
        };

        var result = HeadingLevelResolver.Resolve(blocks, pageText, outline, 0);

        // Should fuzzy-match via containment or Levenshtein
        Assert.Equal(2, result[0]);
    }

    [Fact]
    public void FlattenOutline_ProducesCorrectDepths()
    {
        var outline = new List<OutlineEntry>
        {
            new()
            {
                Title = "Ch 1", Page = 0, Children =
                [
                    new() { Title = "1.1", Page = 1, Children =
                    [
                        new() { Title = "1.1.1", Page = 2, Children = [] },
                    ]},
                ],
            },
            new() { Title = "Ch 2", Page = 3, Children = [] },
        };

        var flat = HeadingLevelResolver.FlattenOutline(outline);

        Assert.Equal(4, flat.Count);
        Assert.Equal(("Ch 1", (int?)0, 1), (flat[0].Title, flat[0].Page, flat[0].Depth));
        Assert.Equal(("1.1", (int?)1, 2), (flat[1].Title, flat[1].Page, flat[1].Depth));
        Assert.Equal(("1.1.1", (int?)2, 3), (flat[2].Title, flat[2].Page, flat[2].Depth));
        Assert.Equal(("Ch 2", (int?)3, 1), (flat[3].Title, flat[3].Page, flat[3].Depth));
    }

    [Fact]
    public void LevenshteinDistance_BasicCases()
    {
        Assert.Equal(0, HeadingLevelResolver.LevenshteinDistance("abc", "abc"));
        Assert.Equal(1, HeadingLevelResolver.LevenshteinDistance("abc", "ab"));
        Assert.Equal(3, HeadingLevelResolver.LevenshteinDistance("", "abc"));
        Assert.Equal(1, HeadingLevelResolver.LevenshteinDistance("kitten", "sitten"));
    }

    private static LayoutBlock MakeBlock(int classId, int order,
        float x = 0, float y = 0, float w = 100, float h = 20)
    {
        return new LayoutBlock
        {
            ClassId = classId,
            Order = order,
            Confidence = 0.9f,
            BBox = new BBox(x, y, w, h),
        };
    }

    private static PageText MakePageText(string text, float x, float y, float w, float h)
    {
        // Create char boxes that place all characters within the bbox
        var charBoxes = new List<CharBox>();
        float charWidth = w / Math.Max(text.Length, 1);
        for (int i = 0; i < text.Length; i++)
        {
            charBoxes.Add(new CharBox(
                i,
                x + i * charWidth,
                y,
                x + (i + 1) * charWidth,
                y + h));
        }
        return new PageText(text, charBoxes);
    }
}
