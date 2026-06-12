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

    [Theory]
    [InlineData("details in Figure A.2 of the appendix", "Figure", "a.2")]
    [InlineData("as TABLE II shows", "Table", "ii")]
    [InlineData("see Table IV for", "Table", "iv")]
    [InlineData("Appendix Figure B contains", "Figure", "b")]
    public void ParseLine_RecognisesAppendixAndRomanNumbers(string line, string kind, string number)
        => Assert.Contains(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_RomanNumeralsAreUppercaseOnly()
        => Assert.Empty(ReferenceIndex.ParseLine("the table is shown"));   // "is" must not read as roman

    [Theory]
    [InlineData("the table I made for this")]    // pronoun after lowercase kind word
    [InlineData("the figure I show next")]
    public void ParseLine_RejectsDigitlessNumbersAfterLowercaseKind(string line)
        => Assert.Empty(ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_AcceptsRomanAfterCapitalisedKind()
        => Assert.Equal([new Ref(RefKind.Table, "ii")], ReferenceIndex.ParseLine("see Table II for"));

    [Theory]
    [InlineData("Figures 2 and 3 show", new[] { "2", "3" })]
    [InlineData("Tables 1, 4 and 6 list", new[] { "1", "4", "6" })]
    [InlineData("Figs. 3–5 illustrate", new[] { "3", "5" })]   // range endpoints
    [InlineData("Tables 4.2 and 4.3 compare", new[] { "4.2", "4.3" })]
    public void ParseLine_ExpandsPluralContinuations(string line, string[] numbers)
        => Assert.Equal(numbers, ReferenceIndex.ParseLine(line).Select(r => r.Number));

    [Fact]
    public void ParseLine_ContinuationStopsAtNonNumber()
        => Assert.Single(ReferenceIndex.ParseLine("Figures 2 and the results show"));

    [Theory]
    [InlineData("Table 1, 95% of cases were correct", "1")]      // singular → no phantom Table 95
    [InlineData("Figure 2 and 2008 observations saw", "2")]      // singular → no phantom Figure 2008
    public void ParseLine_SingularMentionsDoNotExpandContinuations(string line, string only)
    {
        var refs = ReferenceIndex.ParseLine(line);
        Assert.Single(refs);
        Assert.Equal(only, refs[0].Number);
    }

    [Fact]
    public void ParseLine_StartLimit_CatchesMentionSplitAcrossLines()
    {
        // "…see Figure" at the end of the current line, "3 shows…" starting the next.
        string current = "as we see Figure";
        string run = current + " " + "3 shows the effect";
        Assert.Contains(new Ref(RefKind.Figure, "3"), ReferenceIndex.ParseLine(run, current.Length));
    }

    [Fact]
    public void ParseLine_StartLimit_IgnoresMentionsWhollyOnNextLine()
    {
        string current = "the end of a sentence.";
        string run = current + " " + "Figure 3 shows the effect";
        Assert.Empty(ReferenceIndex.ParseLine(run, current.Length));
    }

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

    [Theory]
    [InlineData("TABLE II: Results", "Table", "ii")]
    [InlineData("Figure A.4 — Residual plots", "Figure", "a.4")]
    public void ParseCaptionLabel_RecognisesRomanAndAppendixLabels(string caption, string kind, string number)
        => Assert.Equal(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseCaptionLabel(caption));

    [Theory]
    [InlineData("Figure 3: Convergence", true)]    // colon → caption-like
    [InlineData("Table 4. Parameter estimates", true)]
    [InlineData("Figure 2 — Sample paths", true)]
    [InlineData("Figure 3", true)]                 // bare label (end of text) counts
    [InlineData("Figure 3 shows that the estimator", false)]   // body sentence → rejected
    [InlineData("Fig. 2 - Sample paths", true)]                // spaced hyphen = caption separator
    [InlineData("Figure 3-D printed scaffolds are shown", false)]   // compound hyphen ≠ separator
    public void ParseCaptionLabel_RequirePunctuation_FiltersBodySentences(string text, bool accepted)
        => Assert.Equal(accepted,
            ReferenceIndex.ParseCaptionLabel(text, requirePunctuation: true) is not null);

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
