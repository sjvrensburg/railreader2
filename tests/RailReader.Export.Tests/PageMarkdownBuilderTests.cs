using Xunit;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;

namespace RailReader.Export.Tests;

public class PageMarkdownBuilderTests
{
    [Fact]
    public void Heading_RendersCorrectLevel()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDocTitle, 0),
        };
        var headingLevels = new Dictionary<int, int> { [0] = 1 };
        var pageText = MakePageText("Introduction", blocks[0]);

        var md = PageMarkdownBuilder.Build(blocks, headingLevels, pageText, null, null, null);

        Assert.Contains("# Introduction", md);
    }

    [Fact]
    public void TextBlock_RendersAsParagraph()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(22, 0), // text class
        };
        var pageText = MakePageText("Some paragraph text here.", blocks[0]);

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), pageText, null, null, null);

        Assert.Contains("Some paragraph text here.", md);
        Assert.DoesNotContain("#", md);
    }

    [Fact]
    public void Equation_WithVlm_RendersLatex()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDisplayFormula, 0),
        };
        var vlm = new Dictionary<int, PageMarkdownBuilder.VlmBlockResult>
        {
            [0] = new(0, @"E = mc^2", null),
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, vlm, null, null);

        Assert.Contains("$$E = mc^2$$", md);
    }

    [Fact]
    public void Equation_WithoutVlm_RendersFallback()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDisplayFormula, 0),
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, null, null, null);

        Assert.Contains("[equation]", md);
    }

    [Fact]
    public void Equation_WithoutVlm_WithText_RendersFallbackWithText()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDisplayFormula, 0),
        };
        var pageText = MakePageText("x + y = z", blocks[0]);

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), pageText, null, null, null);

        Assert.Contains("[equation: x + y = z]", md);
    }

    [Fact]
    public void Table_WithVlm_RendersMarkdownTable()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassTable, 0),
        };
        var tableMarkdown = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var vlm = new Dictionary<int, PageMarkdownBuilder.VlmBlockResult>
        {
            [0] = new(0, tableMarkdown, null),
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, vlm, null, null);

        Assert.Contains("| A | B |", md);
    }

    [Fact]
    public void Table_WithoutVlm_WithText_RendersCodeBlock()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassTable, 0),
        };
        var pageText = MakePageText("A B\n1 2", blocks[0]);

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), pageText, null, null, null);

        Assert.Contains("```", md);
        Assert.Contains("A B", md);
    }

    [Fact]
    public void Figure_WithImagePath_RendersImageTag()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassImage, 0),
        };
        var vlm = new Dictionary<int, PageMarkdownBuilder.VlmBlockResult>
        {
            [0] = new(0, "A scatter plot showing correlation", null),
        };
        var figurePaths = new Dictionary<int, string>
        {
            [0] = "figures/fig_p1_b0.png",
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, vlm, null, figurePaths);

        Assert.Contains("![A scatter plot showing correlation](figures/fig_p1_b0.png)", md);
    }

    [Fact]
    public void Figure_WithoutImage_WithDescription_RendersInlineDescription()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassImage, 0),
        };
        var vlm = new Dictionary<int, PageMarkdownBuilder.VlmBlockResult>
        {
            [0] = new(0, "A bar chart of results", null),
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, vlm, null, null);

        Assert.Contains("[figure: A bar chart of results]", md);
    }

    [Fact]
    public void FigureTitle_RendersItalic()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(7, 0), // figure_title
        };
        var pageText = MakePageText("Figure 3: Results overview", blocks[0]);

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), pageText, null, null, null);

        Assert.Contains("*Figure 3: Results overview*", md);
    }

    [Fact]
    public void PageFurniture_IsSkipped()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(12, 0), // header
            MakeBlock(8, 1),  // footer
            MakeBlock(16, 2), // number
            MakeBlock(20, 3), // seal
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, null, null, null);

        Assert.Equal("", md.Trim());
    }

    [Fact]
    public void MultipleBlocks_OrderedCorrectly()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDocTitle, 0, 0, 0, 100, 20),
            MakeBlock(22, 1, 0, 30, 100, 20), // text
        };
        var headingLevels = new Dictionary<int, int> { [0] = 1 };
        var charBoxes = new List<CharBox>();
        charBoxes.AddRange(MakeCharBoxes("Introduction", 0, 0, 100, 20, 0));
        charBoxes.AddRange(MakeCharBoxes("Some body text.", 0, 30, 100, 50, 12));
        var pageText = new PageText("IntroductionSome body text.", charBoxes);

        var md = PageMarkdownBuilder.Build(blocks, headingLevels, pageText, null, null, null);

        var introIdx = md.IndexOf("# Introduction");
        var bodyIdx = md.IndexOf("Some body text.");
        Assert.True(introIdx >= 0);
        Assert.True(bodyIdx > introIdx);
    }

    [Fact]
    public void Annotations_TextNote_RendersBlockquote()
    {
        var blocks = new List<LayoutBlock>();
        var annotations = new PageMarkdownBuilder.PageAnnotations(
            [],
            [new TextNoteAnnotation { Text = "Important point here", X = 10, Y = 10 }]);

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, null, annotations, null);

        Assert.Contains("> **Note:** Important point here", md);
    }

    [Fact]
    public void Annotations_Highlight_RendersWithColor()
    {
        var blocks = new List<LayoutBlock>();
        var annotations = new PageMarkdownBuilder.PageAnnotations(
            [new HighlightAnnotation { Color = "#FFFF00", Rects = [new HighlightRect(10, 10, 100, 20)] }],
            []);

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), null, null, annotations, null);

        Assert.Contains("[highlight: #FFFF00]", md);
    }

    [Fact]
    public void BuildPlainText_WithOutline_InsertsHeadings()
    {
        var outline = new List<OutlineEntry>
        {
            new()
            {
                Title = "Chapter 1",
                Page = 0,
                Children =
                [
                    new() { Title = "Section 1.1", Page = 0, Children = [] },
                ],
            },
        };

        var md = PageMarkdownBuilder.BuildPlainText(
            "Body text of the page.", outline, 0, null);

        Assert.Contains("# Chapter 1", md);
        Assert.Contains("## Section 1.1", md);
        Assert.Contains("Body text of the page.", md);
    }

    [Fact]
    public void BuildPlainText_WithAnnotations()
    {
        var annotations = new PageMarkdownBuilder.PageAnnotations(
            [],
            [new TextNoteAnnotation { Text = "My note", X = 0, Y = 0 }]);

        var md = PageMarkdownBuilder.BuildPlainText("Text.", null, 0, annotations);

        Assert.Contains("> **Note:** My note", md);
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

    private static PageText MakePageText(string text, LayoutBlock block)
    {
        var bbox = block.BBox;
        var charBoxes = MakeCharBoxes(text, bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H, 0);
        return new PageText(text, charBoxes);
    }

    private static List<CharBox> MakeCharBoxes(string text, float x, float y, float right, float bottom, int startIndex)
    {
        float charWidth = (right - x) / Math.Max(text.Length, 1);
        var boxes = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
        {
            boxes.Add(new CharBox(
                startIndex + i,
                x + i * charWidth,
                y,
                x + (i + 1) * charWidth,
                bottom));
        }
        return boxes;
    }
}
