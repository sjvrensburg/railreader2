using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for the char-box-based clustering path and atomic-class handling in
/// <see cref="LineDetector"/>. The pixel-projection path is covered by
/// <see cref="LineDetectionTests"/>.
/// </summary>
public class LineDetectorTests
{
    /// <summary>
    /// Builds a list of CharBoxes for a fake page with <paramref name="lineCount"/>
    /// lines of <paramref name="charsPerLine"/> characters. Each char is approximately
    /// <paramref name="charHeight"/> tall, separated by <paramref name="lineGap"/>
    /// vertical units. The block bbox is set to fully enclose them.
    /// </summary>
    private static (LayoutBlock Block, List<CharBox> Chars) MakeLines(
        int lineCount, int charsPerLine, float charHeight = 10f, float lineGap = 4f,
        float left = 100f, float top = 100f, BlockRole role = BlockRole.Text)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        for (int line = 0; line < lineCount; line++)
        {
            float lineTop = top + line * (charHeight + lineGap);
            float lineBottom = lineTop + charHeight;
            for (int c = 0; c < charsPerLine; c++)
            {
                float charLeft = left + c * 8f;
                chars.Add(new CharBox(idx++, charLeft, lineTop, charLeft + 7f, lineBottom));
            }
        }

        float width = charsPerLine * 8f + 4f;
        float height = lineCount * charHeight + (lineCount - 1) * lineGap;
        var block = new LayoutBlock
        {
            BBox = new BBox(left - 2f, top - 2f, width, height + 4f),
            Role = role,
            Confidence = 0.9f,
        };
        return (block, chars);
    }

    [Fact]
    public void CharClustering_SingleLine()
    {
        var (block, chars) = MakeLines(lineCount: 1, charsPerLine: 8);
        var lines = LineDetector.DetectLinesFromChars(block.BBox, chars);
        Assert.Single(lines);
        Assert.InRange(lines[0].Height, 9f, 12f);
    }

    [Fact]
    public void CharClustering_FiveLines()
    {
        var (block, chars) = MakeLines(lineCount: 5, charsPerLine: 8);
        var lines = LineDetector.DetectLinesFromChars(block.BBox, chars);
        Assert.Equal(5, lines.Count);
    }

    [Fact]
    public void CharClustering_DenseFourteenLines()
    {
        // Same configuration as the pixel-projection 14-line test
        var (block, chars) = MakeLines(lineCount: 14, charsPerLine: 12, charHeight: 10f, lineGap: 4f);
        var lines = LineDetector.DetectLinesFromChars(block.BBox, chars);
        Assert.Equal(14, lines.Count);
    }

    [Fact]
    public void CharClustering_SubscriptsDoNotFragmentLine()
    {
        // Three base chars on a single line, with one subscript shifted down by
        // 3 px (typical subscript displacement)
        var chars = new List<CharBox>
        {
            new(0, 100, 100, 107, 110), // H
            new(1, 108, 103, 112, 113), // ₂ subscript: 3px lower
            new(2, 113, 100, 120, 110), // O
        };
        var bbox = new BBox(98, 98, 25, 18);
        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_SuperscriptsDoNotFragmentLine()
    {
        // x² + y² on one line: superscripts shifted up by 4 px
        var chars = new List<CharBox>
        {
            new(0, 100, 100, 107, 110), // x
            new(1, 108, 96,  112, 105), // ² (shifted up)
            new(2, 115, 100, 122, 110), // +
            new(3, 125, 100, 132, 110), // y
            new(4, 133, 96,  137, 105), // ²
        };
        var bbox = new BBox(98, 94, 42, 18);
        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_FiltersCharsOutsideBlock()
    {
        // Chars from two lines, but block only covers the first
        var (_, chars) = MakeLines(lineCount: 2, charsPerLine: 5);
        var narrowBbox = new BBox(98, 98, 50, 14); // only encloses line 1
        var lines = LineDetector.DetectLinesFromChars(narrowBbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_SkipsDegenerateBoxes()
    {
        // Whitespace chars often have zero-width or zero-height boxes
        var chars = new List<CharBox>
        {
            new(0, 100, 100, 107, 110), // visible
            new(1, 108, 0,   108, 0),   // degenerate (space)
            new(2, 110, 100, 117, 110), // visible
        };
        var bbox = new BBox(98, 98, 25, 14);
        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_EmptyInputReturnsEmpty()
    {
        var bbox = new BBox(0, 0, 100, 100);
        var lines = LineDetector.DetectLinesFromChars(bbox, []);
        Assert.Empty(lines);
    }

    [Fact]
    public void DisplayFormula_KeepsPerLineStructure()
    {
        // Stepwise derivations (γ₁ = Cov(…) / = Cov(…) / = -θσ²) are common in
        // math content and read line-by-line in rail mode. Display formulas
        // therefore must NOT collapse to one atomic line — char clustering
        // should expose the individual lines.
        var (block, chars) = MakeLines(lineCount: 4, charsPerLine: 8);
        block.Role = BlockRole.DisplayMath;

        var lines = LineDetector.DetectLines(block, chars, rgbBytes: [], imgW: 0, imgH: 0, scaleX: 1, scaleY: 1);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void AtomicClass_Table_CollapsesToOneLine()
    {
        var (block, chars) = MakeLines(lineCount: 6, charsPerLine: 10);
        block.Role = BlockRole.Table;

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);
        Assert.Single(lines);
    }

    [Fact]
    public void AtomicClass_Image_CollapsesToOneLine()
    {
        var (block, chars) = MakeLines(lineCount: 3, charsPerLine: 5);
        block.Role = BlockRole.Figure;

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);
        Assert.Single(lines);
    }

    [Fact]
    public void DetectLines_PrefersCharBoxesOverPixels()
    {
        // Char boxes describe two clear lines. Pixel bytes are empty (would
        // produce zero lines via pixel projection). The char path should win.
        var (block, chars) = MakeLines(lineCount: 2, charsPerLine: 6);

        var lines = LineDetector.DetectLines(block, chars, rgbBytes: [], imgW: 0, imgH: 0, scaleX: 1, scaleY: 1);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void DetectLines_FallsBackToPixelsWhenNoChars()
    {
        // No char boxes — the pixel path runs. Build a tiny 20×30 RGB image
        // with three dark bands separated by white gaps; expect three lines.
        const int imgW = 20;
        const int imgH = 30;
        var rgb = new byte[imgW * imgH * 3];
        Array.Fill(rgb, (byte)255); // white background

        void DrawBand(int yStart, int yEnd)
        {
            for (int y = yStart; y < yEnd; y++)
                for (int x = 0; x < imgW; x++)
                {
                    int idx = (y * imgW + x) * 3;
                    rgb[idx] = rgb[idx + 1] = rgb[idx + 2] = 0;
                }
        }

        DrawBand(2, 7);
        DrawBand(12, 17);
        DrawBand(22, 27);

        var block = new LayoutBlock
        {
            BBox = new BBox(0, 0, imgW, imgH),
            Role = BlockRole.Text,
        };

        var lines = LineDetector.DetectLines(block, null, rgb, imgW, imgH, scaleX: 1f, scaleY: 1f);
        Assert.Equal(3, lines.Count);
    }
}
