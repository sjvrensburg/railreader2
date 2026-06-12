using RailReader.Core.Models;
using RailReader2.Services;
using Xunit;
using Ref = RailReader2.Services.ReferenceIndex.Reference;
using RefKind = RailReader2.Services.ReferenceIndex.RefKind;

namespace RailReader.Export.Tests;

public class ReferenceIndexTests
{
    // --- ParseLine: in-text mentions ---

    [Theory]
    [InlineData("as shown in Figure 3, the", "Figure", "3")]
    [InlineData("see Fig. 2.1 for details", "Figure", "2.1")]
    [InlineData("compare Figs. 4a and 5", "Figure", "4a")]
    [InlineData("results in Table 12 indicate", "Table", "12")]
    [InlineData("Tab. 3 lists the parameters", "Table", "3")]
    [InlineData("FIGURE 7 SHOWS", "Figure", "7")]
    [InlineData("see Fig.3 (no space)", "Figure", "3")]
    public void ParseLine_RecognisesReferences(string line, string kind, string number)
    {
        var refs = ReferenceIndex.ParseLine(line);
        Assert.Contains(new Ref(Enum.Parse<RefKind>(kind), number), refs);
    }

    [Fact]
    public void ParseLine_ReturnsAllReferencesInReadingOrder()
    {
        var refs = ReferenceIndex.ParseLine("see Table 1 and Figure 2 for the");
        Assert.Equal([new Ref(RefKind.Table, "1"), new Ref(RefKind.Figure, "2")], refs);
    }

    [Theory]
    [InlineData("the configuration 3 was used")]   // "fig" must not match inside "config"
    [InlineData("a vegetable 5 stew")]             // nor "table" inside "vegetable"
    [InlineData("Figure skating is fun")]          // no number → no reference
    [InlineData("plain text without references")]
    public void ParseLine_IgnoresNonReferences(string line)
        => Assert.Empty(ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_NormalisesSuffixCase()
        => Assert.Equal(
            ReferenceIndex.ParseLine("Figure 4B")[0],
            ReferenceIndex.ParseLine("figure 4b")[0]);

    // --- ParseCaptionLabel: caption-leading labels ---

    [Theory]
    [InlineData("Figure 3: Convergence of the estimator", "Figure", "3")]
    [InlineData("Fig. 2.1 — Sample paths", "Figure", "2.1")]
    [InlineData("Table 4. Parameter estimates", "Table", "4")]
    [InlineData("  Figure 10 Some caption", "Figure", "10")]
    public void ParseCaptionLabel_RecognisesLeadingLabels(string caption, string kind, string number)
        => Assert.Equal(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseCaptionLabel(caption));

    [Theory]
    [InlineData("As Figure 3 shows, ...")]   // label not leading → not a caption for Figure 3
    [InlineData("Estimates by region")]
    [InlineData("")]
    public void ParseCaptionLabel_RejectsNonLabels(string caption)
        => Assert.Null(ReferenceIndex.ParseCaptionLabel(caption));

    // --- NearestTargetBlock: caption ↔ float association ---

    private static PageAnalysis Page(params (BlockRole Role, float X, float Y, float W, float H)[] blocks)
        => new()
        {
            PageWidth = 600,
            PageHeight = 800,
            Blocks = [.. blocks.Select(b => new LayoutBlock
            {
                Role = b.Role,
                BBox = new BBox(b.X, b.Y, b.W, b.H),
            })],
        };

    [Fact]
    public void NearestTargetBlock_PicksFigureDirectlyAboveCaption()
    {
        var page = Page(
            (BlockRole.Figure, 50, 100, 500, 300),    // 0: directly above the caption
            (BlockRole.Caption, 50, 410, 500, 30),    // 1
            (BlockRole.Figure, 50, 600, 500, 150));   // 2: further away
        Assert.Equal(0, ReferenceIndex.NearestTargetBlock(page, 1, RefKind.Figure));
    }

    [Fact]
    public void NearestTargetBlock_MatchesKind_TableCaptionSkipsFigures()
    {
        var page = Page(
            (BlockRole.Figure, 50, 100, 500, 200),    // 0: nearer, but wrong kind
            (BlockRole.Caption, 50, 310, 500, 30),    // 1
            (BlockRole.Table, 50, 360, 500, 200));    // 2
        Assert.Equal(2, ReferenceIndex.NearestTargetBlock(page, 1, RefKind.Table));
    }

    [Fact]
    public void NearestTargetBlock_FigureKindAcceptsCharts()
    {
        var page = Page(
            (BlockRole.Chart, 50, 100, 500, 300),     // 0
            (BlockRole.Caption, 50, 410, 500, 30));   // 1
        Assert.Equal(0, ReferenceIndex.NearestTargetBlock(page, 1, RefKind.Figure));
    }

    [Fact]
    public void NearestTargetBlock_PrefersHorizontalOverlapOverProximity()
    {
        var page = Page(
            (BlockRole.Figure, 320, 95, 250, 100),    // 0: nearer vertically, other column
            (BlockRole.Caption, 20, 210, 250, 30),    // 1: left column
            (BlockRole.Figure, 20, 280, 250, 100));   // 2: overlapping column, slightly further
        Assert.Equal(2, ReferenceIndex.NearestTargetBlock(page, 1, RefKind.Figure));
    }

    [Fact]
    public void NearestTargetBlock_NoCandidate_ReturnsMinusOne()
    {
        var page = Page(
            (BlockRole.Text, 50, 100, 500, 200),
            (BlockRole.Caption, 50, 310, 500, 30));
        Assert.Equal(-1, ReferenceIndex.NearestTargetBlock(page, 1, RefKind.Figure));
    }
}
