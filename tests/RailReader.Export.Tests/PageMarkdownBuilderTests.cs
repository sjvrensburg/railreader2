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
        var blockTexts = new Dictionary<int, string> { [0] = "Introduction" };

        var md = PageMarkdownBuilder.Build(blocks, headingLevels, blockTexts, null, null);

        Assert.Contains("# Introduction", md);
    }

    [Fact]
    public void TextBlock_RendersAsParagraph()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(22, 0), // text class
        };
        var blockTexts = new Dictionary<int, string> { [0] = "Some paragraph text here." };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), blockTexts, null, null);

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

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), new Dictionary<int, string>(), vlm, null);

        Assert.Contains("$$E = mc^2$$", md);
    }

    [Fact]
    public void Equation_WithoutVlm_RendersFallback()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDisplayFormula, 0),
        };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), new Dictionary<int, string>(), null, null);

        Assert.Contains("[equation]", md);
    }

    [Fact]
    public void Equation_WithoutVlm_WithText_RendersFallbackWithText()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassDisplayFormula, 0),
        };
        var blockTexts = new Dictionary<int, string> { [0] = "x + y = z" };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), blockTexts, null, null);

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

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), new Dictionary<int, string>(), vlm, null);

        Assert.Contains("| A | B |", md);
    }

    [Fact]
    public void Table_WithoutVlm_WithText_RendersCodeBlock()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(LayoutConstants.ClassTable, 0),
        };
        var blockTexts = new Dictionary<int, string> { [0] = "A B\n1 2" };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), blockTexts, null, null);

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

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), new Dictionary<int, string>(), vlm, figurePaths);

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

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), new Dictionary<int, string>(), vlm, null);

        Assert.Contains("[figure: A bar chart of results]", md);
    }

    [Fact]
    public void FigureTitle_RendersItalic()
    {
        var blocks = new List<LayoutBlock>
        {
            MakeBlock(7, 0), // figure_title
        };
        var blockTexts = new Dictionary<int, string> { [0] = "Figure 3: Results overview" };

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), blockTexts, null, null);

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

        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), new Dictionary<int, string>(), null, null);

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
        var blockTexts = new Dictionary<int, string>
        {
            [0] = "Introduction",
            [1] = "Some body text.",
        };

        var md = PageMarkdownBuilder.Build(blocks, headingLevels, blockTexts, null, null);

        var introIdx = md.IndexOf("# Introduction");
        var bodyIdx = md.IndexOf("Some body text.");
        Assert.True(introIdx >= 0);
        Assert.True(bodyIdx > introIdx);
    }

    [Fact]
    public void Annotations_TextNote_RendersBlockquote()
    {
        var sb = new System.Text.StringBuilder();
        var annotations = new PageMarkdownBuilder.PageAnnotations(
            [],
            [new TextNoteAnnotation { Text = "Important point here", X = 10, Y = 10 }]);

        PageMarkdownBuilder.AppendAnnotations(sb, annotations);

        Assert.Contains("> **Note:** Important point here", sb.ToString());
    }

    [Fact]
    public void Annotations_Highlight_WithoutText_RendersColorMarker()
    {
        var sb = new System.Text.StringBuilder();
        var annotations = new PageMarkdownBuilder.PageAnnotations(
            [new HighlightAnnotation { Color = "#FFFF00", Rects = [new HighlightRect(10, 10, 100, 20)] }],
            []);

        PageMarkdownBuilder.AppendAnnotations(sb, annotations);

        Assert.Contains("[highlight: #FFFF00]", sb.ToString());
    }

    [Fact]
    public void Annotations_Highlight_WithText_ExtractsContent()
    {
        var sb = new System.Text.StringBuilder();
        var annotations = new PageMarkdownBuilder.PageAnnotations(
            [new HighlightAnnotation { Color = "#FFFF00", Rects = [new HighlightRect(10, 10, 100, 20)] }],
            []);
        var pageText = MakePageText("highlighted text", 10, 10, 110, 30);

        PageMarkdownBuilder.AppendAnnotations(sb, annotations, pageText);

        var result = sb.ToString();
        Assert.Contains("> highlighted text", result);
        Assert.Contains("<!-- highlight: #FFFF00 -->", result);
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
        var flatOutline = HeadingLevelResolver.FlattenOutline(outline);
        var pageText = new PageText("Body text of the page.", []);

        var md = PageMarkdownBuilder.BuildPlainText(pageText, flatOutline, 0, null);

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
        var pageText = new PageText("Text.", []);
        var flatOutline = HeadingLevelResolver.FlattenOutline([]);

        var md = PageMarkdownBuilder.BuildPlainText(pageText, flatOutline, 0, annotations);

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

    private static PageText MakePageText(string text, float x, float y, float right, float bottom)
    {
        float charWidth = (right - x) / Math.Max(text.Length, 1);
        var boxes = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
        {
            boxes.Add(new CharBox(
                i,
                x + i * charWidth,
                y,
                x + (i + 1) * charWidth,
                bottom));
        }
        return new PageText(text, boxes);
    }
}
