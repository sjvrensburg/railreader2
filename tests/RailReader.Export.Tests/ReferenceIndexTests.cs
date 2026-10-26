using RailReader.Core.Models;
using RailReader2.References;
using Xunit;
using Ref = RailReader2.References.ReferenceIndex.Reference;
using RefKind = RailReader2.References.ReferenceIndex.RefKind;

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

    // --- ParseLine: chapter-dash numbering ("Figure 3-1") ---

    [Theory]
    [InlineData("see Figure 3-1 for the setup", "Figure", "3-1")]
    [InlineData("as shown in Table 2-4 above", "Table", "2-4")]
    public void ParseLine_RecognisesChapterDashNumbers(string line, string kind, string number)
        => Assert.Contains(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_ChapterDashNumbersDoNotCollide()
    {
        // Distinct chapter-numbered figures must not all truncate to the same "3" reference.
        var a = ReferenceIndex.ParseLine("Figure 3-1 shows the setup")[0];
        var b = ReferenceIndex.ParseLine("Figure 3-2 shows the result")[0];
        var c = ReferenceIndex.ParseLine("Figure 3-3 shows the residuals")[0];
        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ParseLine_ChapterDashSuffixNotAppliedToPluralRanges()
        // Plural + hyphen is still the range separator, not a chapter number.
        => Assert.Equal(
            [new Ref(RefKind.Figure, "3"), new Ref(RefKind.Figure, "5")],
            ReferenceIndex.ParseLine("Figures 3-5 compare"));

    // --- ParseLine: singular lettered sub-part continuations ("Fig 3a and 3b") ---

    [Fact]
    public void ParseLine_ExpandsSingularLetteredSubPartSeries()
        => Assert.Equal(
            [new Ref(RefKind.Figure, "3a"), new Ref(RefKind.Figure, "3b")],
            ReferenceIndex.ParseLine("compare Fig 3a and 3b closely"));

    [Fact]
    public void ParseLine_SingularLetteredSeriesStopsAtNonLetteredNumber()
        // "and 4" has no letter suffix, so it must not be swept in as prose safety would demand.
        => Assert.Equal(
            [new Ref(RefKind.Figure, "3a")],
            ReferenceIndex.ParseLine("see Fig 3a and 4 for details"));

    [Fact]
    public void ParseLine_ExpandsSingularUppercaseLetteredSubPartSeries()
        // Uppercase sub-figure letters ("Fig 4A and 4B") are as valid as lowercase ("4a"/"4b") — see
        // ParseLine_NormalisesSuffixCase, which already treats "Figure 4B" as a normal single label.
        => Assert.Equal(
            [new Ref(RefKind.Figure, "4a"), new Ref(RefKind.Figure, "4b")],
            ReferenceIndex.ParseLine("compare Fig 4A and 4B closely"));

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
    [InlineData("Figure 3-1: Convergence of the estimator", "Figure", "3-1")]
    [InlineData("Table 2-4. Parameter estimates", "Table", "2-4")]
    public void ParseCaptionLabel_RecognisesChapterDashLabels(string caption, string kind, string number)
        => Assert.Equal(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseCaptionLabel(caption));

    [Fact]
    public void ParseCaptionLabel_ChapterDashLabelMatchesInTextMention()
        // The bug this fixes: a mention and its caption must resolve to the SAME reference key.
        => Assert.Equal(
            ReferenceIndex.ParseLine("see Figure 3-1 for the setup")[0],
            ReferenceIndex.ParseCaptionLabel("Figure 3-1: Convergence of the estimator"));

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

    // --- numbered statements: mentions ---

    [Theory]
    [InlineData("see Theorem 2 for", "Theorem", "2")]
    [InlineData("by Lemma 3.1 we", "Lemma", "3.1")]
    [InlineData("Proposition 2.16 follows", "Proposition", "2.16")]
    [InlineData("as in Definition 2.1 above", "Definition", "2.1")]
    [InlineData("the active frame of Ex. 3 is", "Example", "3")]
    [InlineData("In Def. 13, for each", "Definition", "13")]
    [InlineData("By Prop. 2 the log", "Proposition", "2")]
    [InlineData("see Remark 2.15 and", "Remark", "2.15")]
    [InlineData("Corollary 4 gives", "Corollary", "4")]
    [InlineData("solve Exercise 3.1 first", "Exercise", "3.1")]
    public void ParseLine_RecognisesStatementKinds(string line, string kind, string number)
        => Assert.Contains(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_ExpandsPluralStatementContinuations()
        => Assert.Equal(["2.12", "2.13"],
            ReferenceIndex.ParseLine("extension operations (Definitions 2.12 and 2.13). The").Select(r => r.Number));

    [Theory]
    [InlineData("for example 3 items were")]            // lower-case prose
    [InlineData("an ex 3 minutes ago")]
    [InlineData("Theorem 1 in [4] shows")]              // another work's statement
    [InlineData("Definition 17 of Smith et al. [3]")]
    [InlineData("Lemma 2 from Jones (2019) states")]
    [InlineData("Theorem A states that")]               // digitless
    [InlineData("Example I presented earlier")]
    public void ParseLine_IgnoresStatementLookalikes(string line)
        => Assert.Empty(ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_ForeignCitationAlsoSkipsFloats()
        => Assert.Empty(ReferenceIndex.ParseLine("shown in Figure 2 of [7]"));

    // --- numbered statements: headers ---

    [Theory]
    [InlineData("Definition 1. (Test systems) Let D be", "Definition", "1")]
    [InlineData("Definition 2.3 (match). A match m", "Definition", "2.3")]
    [InlineData("Theorem 1. If a D-reachability", "Theorem", "1")]
    [InlineData("Example 2.2. We introduce here", "Example", "2.2")]
    [InlineData("Exercise 3.1 [Uncorrelated does not imply independent]", "Exercise", "3.1")]
    public void ParseStatementHeader_RecognisesHeaders(string line, string kind, string number)
        => Assert.Equal(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseStatementHeader(line));

    [Theory]
    [InlineData("Theorem 1 shows that the algorithm")]
    [InlineData("see Definition 3. above")]             // not at the line start
    [InlineData("Definition 17 of [4]")]
    [InlineData("Examples are given below")]
    [InlineData("Theorem")]
    public void ParseStatementHeader_RejectsBodyText(string line)
        => Assert.Null(ReferenceIndex.ParseStatementHeader(line));

    // --- equations: mentions ---

    [Theory]
    [InlineData("as shown in Equation 3.28, we", "3.28")]
    [InlineData("from Equation (3.12) we get", "3.12")]
    [InlineData("the problem in equation (20) is", "20")]
    [InlineData("see Eq. (4) above", "4")]
    [InlineData("by (3.2) the claim", "3.2")]
    [InlineData("using the formula (2) as", "2")]
    [InlineData("Equation (28) represents", "28")]
    public void ParseLine_RecognisesEquationMentions(string line, string number)
        => Assert.Contains(new Ref(RefKind.Equation, number), ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_ExpandsPluralEquationMentions()
        => Assert.Equal(["3.2", "4", "6"],
            ReferenceIndex.ParseLine("see Eq. (3.2) and Eqs. (4)–(6) for").Select(r => r.Number));

    [Theory]
    [InlineData("(1) first, (2) second")]                    // list markers open their clause
    [InlineData("(1) f ∈ W follows")]                        // a proof step
    [InlineData("a bound of order O(1) holds")]              // attached to a word
    [InlineData("the AR(1) model")]
    [InlineData("where f(0) = 1")]
    [InlineData("Vaswani et al. (2017) showed")]            // a year
    [InlineData("Random Forest (0.45), and OLS (0.28)")]     // decimal values
    [InlineData("Econometrica, 44(2):461–466")]              // journal issue, attached
    [InlineData("see Table 1 (2) below")]                    // after a digit
    public void ParseLine_IgnoresEquationLookalikes(string line)
        => Assert.DoesNotContain(ReferenceIndex.ParseLine(line), r => r.Kind == RefKind.Equation);

    // --- citations name results of the cited work ---

    [Theory]
    [InlineData("it follows from [12, Theorem 3.2] that")]
    [InlineData("by [IK99, Lemma 1.15] we")]
    [InlineData("From Amemiya [2, eqs. (5) and (6)] it follows")]
    [InlineData("(see Goldberger [12, p. 165, eq. (4.21)])")]
    [InlineData("as (Smith 2019, Theorem 3) proves")]
    [InlineData("as (Smith et al. 2019, Eq. (4)) shows")]
    public void ParseLine_IgnoresResultsNamedInsideACitation(string line)
        => Assert.Empty(ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseLine_AnIntervalIsNotACitation()
        => Assert.Contains(new Ref(RefKind.Theorem, "2"), ReferenceIndex.ParseLine("on [0, 1] Theorem 2 gives"));

    [Fact]
    public void ParseLine_ParenthesisedStatementNumber()
        => Assert.Equal([new Ref(RefKind.Theorem, "3.2")], ReferenceIndex.ParseLine("by Theorem (3.2) we"));

    // --- contents entries are not statement headers ---

    [Theory]
    [InlineData("Definition 6.1 . . . . . . . . 12")]
    [InlineData("Theorem 3.2. Compactness ........ 41")]
    [InlineData("Lemma 4 …… 57")]
    public void ParseStatementHeader_RejectsContentsEntries(string line)
        => Assert.Null(ReferenceIndex.ParseStatementHeader(line));

    [Fact]
    public void ParseStatementHeader_AnEllipsisInTheBodyIsStillAHeader()
        => Assert.Equal(new Ref(RefKind.Definition, "1"),
            ReferenceIndex.ParseStatementHeader("Definition 1. Let x1, …, xn be given"));

    // --- guards found by auditing 30 unseen papers ---

    [Theory]
    [InlineData("C.8 Proof of Lemma 3.8 . . . . . . . . . . . . 41")]    // contents entry
    [InlineData("1.2 Theorem 4 and Figure 2 …… 17")]
    public void ParseLine_ContentsLinesCiteNothing(string line)
        => Assert.Empty(ReferenceIndex.ParseLine(line));

    [Fact]
    public void ParseStatementHeader_SubItemMarkerIsNotATitle()
    {
        Assert.Null(ReferenceIndex.ParseStatementHeader("Proposition 1(d). Let A be randomized"));
        Assert.Equal(new Ref(RefKind.Proposition, "1"), ReferenceIndex.ParseStatementHeader("Proposition 1 (Identification). Suppose"));
    }

    [Theory]
    [InlineData("the dependence on µ (2) and π (1) is")]     // superscript indices after a symbol
    [InlineData("it verifies conditions (1) and (3) of")]    // numbered conditions
    [InlineData("the estimator satisfies (1)–(3): then")]    // a range of items
    [InlineData("this shows (1) of Assumption 2.2 holds")]   // an item of a named assumption
    [InlineData("in each case (1) holds")]
    public void ParseLine_NumberedItemsAreNotEquations(string line)
        => Assert.DoesNotContain(ReferenceIndex.ParseLine(line), r => r.Kind == RefKind.Equation);

    [Fact]
    public void ParseLine_ChainedEquationMentionsKeepTheirWord()
        => Assert.Equal(["2", "3"],
            ReferenceIndex.ParseLine("using the formula (2) and (3) we").Where(r => r.Kind == RefKind.Equation).Select(r => r.Number));

    // --- guards found by auditing a second batch of unseen papers ---

    [Theory]
    [InlineData("so that Dn = 1 + oP (1) and then")]
    [InlineData("the remainder is OP (1) uniformly")]
    [InlineData("we have log (2) bits and Var (1) terms")]
    public void ParseLine_AsymptoticNotationAndFunctionsAreNotEquations(string line)
        => Assert.DoesNotContain(ReferenceIndex.ParseLine(line), r => r.Kind == RefKind.Equation);

    [Fact]
    public void ParseLine_AppendixLabelsWithAHyphenAreOneNumber()
        => Assert.Equal([new Ref(RefKind.Table, "a-2"), new Ref(RefKind.Table, "b-1")],
            ReferenceIndex.ParseLine("method: Tables A-2 and B-1"));

    [Fact]
    public void ParseLine_NonAsciiCitationKeysAreStillCitations()
        => Assert.Empty(ReferenceIndex.ParseLine("by John–Nirenberg [Lê22, Theorem 3.4] one"));

    [Theory]
    [InlineData("Lemma C.1. Under Assumption A", "Lemma", "c.1")]
    [InlineData("Theorem A.2 (Main result). Let", "Theorem", "a.2")]
    public void ParseStatementHeader_AppendixLetteredNumbers(string line, string kind, string number)
        => Assert.Equal(new Ref(Enum.Parse<RefKind>(kind), number), ReferenceIndex.ParseStatementHeader(line));
}
