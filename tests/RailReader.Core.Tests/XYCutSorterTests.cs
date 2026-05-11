using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class XYCutSorterTests
{
    private static LayoutBlock Block(float x, float y, float w, float h, int classId = 4)
        => new() { BBox = new BBox(x, y, w, h), Confidence = 0.9f, ClassId = classId };

    private static int[] Orders(IEnumerable<LayoutBlock> blocks)
        => blocks.Select(b => b.Order).ToArray();

    [Fact]
    public void SingleColumn_PreservesTopToBottomOrder()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(50, 300, 500, 50),  // bottom
            Block(50, 100, 500, 50),  // top
            Block(50, 200, 500, 50),  // middle
        };
        XYCutSorter.SortInPlace(blocks);

        Assert.Equal(100, blocks[0].BBox.Y);
        Assert.Equal(200, blocks[1].BBox.Y);
        Assert.Equal(300, blocks[2].BBox.Y);
        Assert.Equal(new[] { 0, 1, 2 }, Orders(blocks));
    }

    [Fact]
    public void TwoColumns_LeftFullyBeforeRight()
    {
        // 612pt page, two columns with a 30pt gap at x=300..330
        var leftTop = Block(50, 100, 250, 200);
        var leftBot = Block(50, 320, 250, 200);
        var rightTop = Block(330, 100, 250, 200);
        var rightBot = Block(330, 320, 250, 200);

        var blocks = new List<LayoutBlock> { rightBot, leftTop, rightTop, leftBot };
        XYCutSorter.SortInPlace(blocks);

        Assert.Same(leftTop, blocks[0]);
        Assert.Same(leftBot, blocks[1]);
        Assert.Same(rightTop, blocks[2]);
        Assert.Same(rightBot, blocks[3]);
    }

    [Fact]
    public void HeaderAboveTwoColumns_HeaderFirstThenLeftThenRight()
    {
        var header = Block(50, 50, 540, 30);
        var leftTop = Block(50, 120, 250, 200);
        var leftBot = Block(50, 340, 250, 200);
        var rightTop = Block(330, 120, 250, 200);
        var rightBot = Block(330, 340, 250, 200);

        var blocks = new List<LayoutBlock> { rightBot, leftTop, header, rightTop, leftBot };
        XYCutSorter.SortInPlace(blocks);

        Assert.Same(header, blocks[0]);
        Assert.Same(leftTop, blocks[1]);
        Assert.Same(leftBot, blocks[2]);
        Assert.Same(rightTop, blocks[3]);
        Assert.Same(rightBot, blocks[4]);
    }

    [Fact]
    public void NarrowPageNumber_DoesNotBlockColumnSplit()
    {
        // Two columns plus a narrow centered page number that spans the gap
        var leftTop = Block(50, 100, 250, 200);
        var rightTop = Block(330, 100, 250, 200);
        var pageNumber = Block(295, 750, 30, 12);  // narrow, low

        var blocks = new List<LayoutBlock> { pageNumber, leftTop, rightTop };
        XYCutSorter.SortInPlace(blocks);

        // Left column comes before right column; page number lands at the end
        // (it's below both columns, so any reasonable order puts it last).
        Assert.Same(leftTop, blocks[0]);
        Assert.Same(rightTop, blocks[1]);
        Assert.Same(pageNumber, blocks[2]);
    }

    [Fact]
    public void EmptyList_NoExceptions()
    {
        var blocks = new List<LayoutBlock>();
        XYCutSorter.SortInPlace(blocks);
        Assert.Empty(blocks);
    }

    [Fact]
    public void SingleBlock_OrderZero()
    {
        var blocks = new List<LayoutBlock> { Block(0, 0, 100, 100) };
        XYCutSorter.SortInPlace(blocks);
        Assert.Equal(0, blocks[0].Order);
    }

    [Fact]
    public void OverlappingBlocks_FallsBackToYSortWithoutCrashing()
    {
        // Same position — no cuts can be made; just need to not loop forever
        var a = Block(50, 100, 200, 50);
        var b = Block(50, 100, 200, 50);
        var blocks = new List<LayoutBlock> { a, b };
        XYCutSorter.SortInPlace(blocks);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new[] { 0, 1 }, Orders(blocks));
    }

    [Fact]
    public void ThreeColumns_SortedLeftToRight()
    {
        var c1 = Block(50, 100, 150, 400);
        var c2 = Block(230, 100, 150, 400);
        var c3 = Block(410, 100, 150, 400);
        var blocks = new List<LayoutBlock> { c3, c1, c2 };
        XYCutSorter.SortInPlace(blocks);
        Assert.Same(c1, blocks[0]);
        Assert.Same(c2, blocks[1]);
        Assert.Same(c3, blocks[2]);
    }

    [Fact]
    public void VerticalMarginText_DoesNotBlockColumnSplit()
    {
        // Mimics IEEE/ICASSP layout: a tall narrow rotated info bar on the left
        // margin running the full page height. Naive XY-cut sees prevBottom
        // dominate the page and finds no horizontal gap, then sorts by Y and
        // interleaves columns. The tall-narrow cross-layout pre-mask + symmetric
        // narrow filter should remove it before recursion.
        var margin = Block(20, 50, 15, 700);             // tall, ~2% page width
        var title = Block(100, 50, 400, 30);             // full width header
        var leftTop = Block(50, 120, 200, 250);
        var leftBot = Block(50, 400, 200, 250);
        var rightTop = Block(280, 120, 200, 250);
        var rightBot = Block(280, 400, 200, 250);

        var blocks = new List<LayoutBlock> { rightBot, margin, leftTop, title, rightTop, leftBot };
        XYCutSorter.SortInPlace(blocks);

        // Body order: left column, then right column. Title and margin may be
        // inserted by Y around them — we only care that left precedes right.
        int leftTopIdx = blocks.IndexOf(leftTop);
        int leftBotIdx = blocks.IndexOf(leftBot);
        int rightTopIdx = blocks.IndexOf(rightTop);
        int rightBotIdx = blocks.IndexOf(rightBot);

        Assert.True(leftTopIdx < leftBotIdx, "leftTop should precede leftBot");
        Assert.True(leftBotIdx < rightTopIdx, "left column should precede right column");
        Assert.True(rightTopIdx < rightBotIdx, "rightTop should precede rightBot");
    }

    [Fact]
    public void TightColumnGap_WithPageNumberBetweenColumns()
    {
        // Reproduces the ICASSP failure: 4.3pt column gap with a narrow page
        // number sitting roughly at the column boundary. Standard XY-cut with
        // a 5pt min-gap threshold would fall back to y-sort. The narrow-outlier
        // filter on the vertical cut combined with a sub-5pt threshold should
        // still find the column boundary.
        // Coordinates are in PDF points (612x792 page).
        var title = Block(79, 84, 457, 43);                    // wide+short title
        var author = Block(80, 132, 453, 26);
        var absHeading = Block(140, 215, 71, 20);              // ABSTRACT centered above left col
        var rTop = Block(309, 217, 258, 52);                   // right col top
        var lAbstract = Block(45, 237, 259, 244);              // left col abstract body
        var rMid = Block(309, 269, 258, 143);
        var rBot = Block(309, 412, 260, 136);
        var keyword = Block(48, 481, 256, 31);
        var introHead = Block(122, 521, 110, 20);
        var lIntro = Block(46, 546, 260, 163);                 // left col intro body
        var rIntroTop = Block(322, 554, 247, 91);
        var rIntroBot = Block(325, 648, 243, 80);
        var pageNum = Block(284, 736, 41, 19);                 // narrow, between cols by centerX
        var legal = Block(54, 759, 496, 19);                   // wide+short bottom legal

        var blocks = new List<LayoutBlock>
        {
            pageNum, rIntroBot, lIntro, rTop, title, author, absHeading,
            lAbstract, rMid, rBot, keyword, introHead, rIntroTop, legal,
        };
        XYCutSorter.SortInPlace(blocks);

        // Body order: title block first (header is wide+short cross-layout),
        // then LEFT column completely (ABSTRACT, abstract body, keywords,
        // intro heading, intro body), then RIGHT column completely.
        int absHeadingIdx = blocks.IndexOf(absHeading);
        int lAbstractIdx = blocks.IndexOf(lAbstract);
        int keywordIdx = blocks.IndexOf(keyword);
        int introHeadIdx = blocks.IndexOf(introHead);
        int lIntroIdx = blocks.IndexOf(lIntro);
        int rTopIdx = blocks.IndexOf(rTop);
        int rMidIdx = blocks.IndexOf(rMid);
        int rBotIdx = blocks.IndexOf(rBot);

        // Left column is in reading order
        Assert.True(absHeadingIdx < lAbstractIdx, "abstract heading → abstract body");
        Assert.True(lAbstractIdx < keywordIdx, "abstract body → Index Terms");
        Assert.True(keywordIdx < introHeadIdx, "Index Terms → INTRO");
        Assert.True(introHeadIdx < lIntroIdx, "INTRO → intro body left");

        // Right column is in reading order
        Assert.True(rTopIdx < rMidIdx && rMidIdx < rBotIdx,
                    "right column top → mid → bottom");

        // The whole left column comes before the whole right column
        Assert.True(lIntroIdx < rTopIdx, "left column fully before right column");
    }

    [Fact]
    public void FullWidthTitle_OverTwoColumns_TitleFirstThenColumnsInOrder()
    {
        // Title spans both columns horizontally — without cross-layout pre-mask,
        // vertical cut fails (title bridges the column gap).
        var title = Block(50, 50, 540, 40);              // full width
        var leftTop = Block(50, 120, 250, 200);
        var leftBot = Block(50, 340, 250, 200);
        var rightTop = Block(330, 120, 250, 200);
        var rightBot = Block(330, 340, 250, 200);

        var blocks = new List<LayoutBlock> { rightBot, leftTop, title, rightTop, leftBot };
        XYCutSorter.SortInPlace(blocks);

        Assert.Same(title, blocks[0]);
        Assert.Same(leftTop, blocks[1]);
        Assert.Same(leftBot, blocks[2]);
        Assert.Same(rightTop, blocks[3]);
        Assert.Same(rightBot, blocks[4]);
    }

    [Fact]
    public void TwoColumns_WithMultipleBlocksPerColumn()
    {
        // 4 blocks per column, ensure within-column top-to-bottom order
        var blocks = new List<LayoutBlock>();
        var expected = new List<LayoutBlock>();
        for (int i = 0; i < 4; i++)
        {
            var l = Block(50, 100 + i * 60, 250, 50);
            expected.Add(l);
            blocks.Add(l);
        }
        for (int i = 0; i < 4; i++)
        {
            var r = Block(330, 100 + i * 60, 250, 50);
            expected.Add(r);
            blocks.Add(r);
        }
        // Shuffle a bit so we know the sort actually did something
        (blocks[0], blocks[7]) = (blocks[7], blocks[0]);
        (blocks[2], blocks[5]) = (blocks[5], blocks[2]);

        XYCutSorter.SortInPlace(blocks);

        for (int i = 0; i < expected.Count; i++)
            Assert.Same(expected[i], blocks[i]);
    }
}
