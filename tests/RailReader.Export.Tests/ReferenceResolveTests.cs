using RailReader.Core.Models;
using RailReader2.References;
using Xunit;
using Ref = RailReader2.References.ReferenceIndex.Reference;
using RefKind = RailReader2.References.ReferenceIndex.RefKind;

namespace RailReader.Export.Tests;

/// <summary>Resolve-level behaviour of <see cref="ReferenceIndex"/> over an in-memory document.</summary>
public class ReferenceResolveTests
{
    private sealed class FakeDoc(int pageCount) : IReferenceDocument
    {
        private readonly Dictionary<int, (PageAnalysis Analysis, PageText Text)> _pages = [];

        public int PageCount => pageCount;
        public int AnalysedPageCount => _pages.Count;
        public bool TryGetAnalysis(int page, out PageAnalysis analysis)
        {
            bool found = _pages.TryGetValue(page, out var e);
            analysis = e.Analysis;
            return found;
        }
        public PageText GetText(int page) => _pages[page].Text;

        /// <summary>A page of blocks; <paramref name="captions"/> maps block index → its text.</summary>
        public FakeDoc Add(int page, (BlockRole Role, float X, float Y, float W, float H)[] blocks,
            Dictionary<int, string>? captions = null)
        {
            var analysis = new PageAnalysis
            {
                PageWidth = 600,
                PageHeight = 800,
                Blocks = [.. blocks.Select(b => new LayoutBlock { Role = b.Role, BBox = new BBox(b.X, b.Y, b.W, b.H) })],
            };
            var text = new System.Text.StringBuilder();
            List<CharBox> boxes = [];
            foreach (var (index, caption) in captions ?? [])
            {
                // Each newline-separated piece is one detected line: a band of the block's height.
                var b = blocks[index];
                var pieces = caption.Split('\n');
                float lh = b.H / pieces.Length;
                for (int li = 0; li < pieces.Length; li++)
                {
                    string piece = pieces[li] + " ";
                    float cw = b.W / piece.Length;
                    for (int i = 0; i < piece.Length; i++)
                        boxes.Add(new CharBox(text.Length + i, b.X + i * cw, b.Y + li * lh, b.X + (i + 1) * cw, b.Y + (li + 1) * lh));
                    text.Append(piece);
                    analysis.Blocks[index].Lines.Add(new LineInfo(b.Y + (li + 0.5f) * lh, lh, b.X, b.W, []));
                }
            }
            _pages[page] = (analysis, new PageText(text.ToString(), boxes));
            return this;
        }

        /// <summary>Analyse every page not added yet (as empty), as when a whole document is analysed.</summary>
        public FakeDoc Complete()
        {
            for (int p = 0; p < pageCount; p++)
                if (!_pages.ContainsKey(p)) Add(p, []);
            return this;
        }

        /// <summary>A one-figure page: the float above its caption.</summary>
        public FakeDoc AddFigure(int page, string label)
            => Add(page, [(BlockRole.Figure, 50, 100, 500, 300), (BlockRole.Caption, 50, 410, 500, 20)],
                new() { [1] = label });
    }

    private static ReferenceIndex.Target? Resolve(ReferenceIndex index, IReferenceDocument doc, Ref r, int near)
    {
        // Resolve indexes a bounded number of pages per call; keep going like the app's retries do.
        for (int i = 0; i <= doc.PageCount; i++)
        {
            var t = index.Resolve(doc, r, near, out bool incomplete);
            if (t is not null || !incomplete) return t;
        }
        return null;
    }

    // --- panel mentions ---

    [Fact]
    public void PanelMention_ResolvesToItsParentFigure()
    {
        var doc = new FakeDoc(3).AddFigure(1, "Figure 2: Analysis of CV.");
        var t = Resolve(new(), doc, new Ref(RefKind.Figure, "2b"), 1);
        Assert.Equal(new ReferenceIndex.Target(1, 0, 1), t);
    }

    [Fact]
    public void PanelMention_PrefersAnExactlyLetteredCaption()
    {
        var doc = new FakeDoc(4).AddFigure(0, "Figure 2: Parent.").AddFigure(2, "Figure 2a: Panel.");
        var t = Resolve(new(), doc, new Ref(RefKind.Figure, "2a"), 1);
        Assert.Equal(2, t!.Value.Page);
    }

    [Fact]
    public void PanelMention_WithNoParentAbstains()
    {
        var doc = new FakeDoc(3).AddFigure(1, "Figure 3: Other.");
        Assert.Null(Resolve(new(), doc, new Ref(RefKind.Figure, "2b"), 1));
    }

    [Fact]
    public void RomanNumeralIsNotAPanel()
    {
        var doc = new FakeDoc(3).AddFigure(1, "Figure I: One.");
        Assert.Null(Resolve(new(), doc, new Ref(RefKind.Figure, "ii"), 1));
    }

    // --- distance cap on flat numbers ---

    [Fact]
    public void FlatNumber_BeyondTheCapAbstains()
    {
        var doc = new FakeDoc(40).AddFigure(2, "Figure 4: Another paper's.");
        Assert.Null(Resolve(new ReferenceIndex { MaxFlatNumberPageDistance = 12 }, doc, new Ref(RefKind.Figure, "4"), 30));
    }

    [Fact]
    public void FlatNumber_WithinTheCapResolves()
    {
        var doc = new FakeDoc(40).AddFigure(20, "Figure 4: Mine.");
        Assert.Equal(20, Resolve(new ReferenceIndex { MaxFlatNumberPageDistance = 12 }, doc, new Ref(RefKind.Figure, "4"), 30)!.Value.Page);
    }

    [Fact]
    public void FlatNumber_CappedMissDoesNotHideAFloatNearAnotherPage()
    {
        // "Figure 4" is absent near page 2 (capped miss, cached) but present near page 30.
        var doc = new FakeDoc(40).AddFigure(30, "Figure 4: Mine.");
        var index = new ReferenceIndex { MaxFlatNumberPageDistance = 12 };
        var r = new Ref(RefKind.Figure, "4");
        Assert.Null(Resolve(index, doc, r, 2));
        Assert.Equal(30, Resolve(index, doc, r, 29)!.Value.Page);
    }

    [Fact]
    public void ScopedNumber_IsNotCapped()
    {
        var doc = new FakeDoc(60).AddFigure(2, "Figure 4.1: Chapter figure.");
        Assert.Equal(2, Resolve(new ReferenceIndex { MaxFlatNumberPageDistance = 12 }, doc, new Ref(RefKind.Figure, "4.1"), 50)!.Value.Page);
    }

    // --- nearest wins even when a farther page is already indexed ---

    [Fact]
    public void Nearest_IsNotReplacedByAFartherAlreadyIndexedPage()
    {
        // The same scoped label on page 5 and 25. Indexing from page 25 caches it; a later lookup from
        // page 14 runs out of per-call budget before reaching page 5 and must not fall through to the
        // cached page 25 (11 pages away) when page 5 (9 away) is the nearer.
        var doc = new FakeDoc(31);
        for (int p = 0; p < 31; p++) doc.Add(p, [(BlockRole.Text, 50, 100, 500, 100)]);
        doc.AddFigure(5, "Figure 7.1: Near.").AddFigure(25, "Figure 7.1: Far.");
        var index = new ReferenceIndex();
        var r = new Ref(RefKind.Figure, "7.1");

        Assert.Equal(25, Resolve(index, doc, r, 26)!.Value.Page);
        Assert.Equal(5, Resolve(index, doc, r, 14)!.Value.Page);
    }

    // --- stacked floats ---

    [Fact]
    public void StackedFloats_EachCaptionKeepsItsOwnFloat()
    {
        // Two floats stacked, captions below. Caption A is a little nearer float B (16) than its own
        // float A (20), but caption B is nearer B still (10), so B is B's and A binds to A.
        var doc = new FakeDoc(1).Add(0,
        [
            (BlockRole.Figure, 50, 100, 500, 200),   // 0: float A, ends y=300
            (BlockRole.Caption, 50, 320, 500, 10),   // 1: caption A, y=320..330
            (BlockRole.Figure, 50, 346, 500, 200),   // 2: float B, 346..546
            (BlockRole.Caption, 50, 556, 500, 10),   // 3: caption B
        ],
        new() { [1] = "Figure 17: A.", [3] = "Figure 18: B." });
        var index = new ReferenceIndex();
        Assert.Equal(0, Resolve(index, doc, new Ref(RefKind.Figure, "17"), 0)!.Value.TargetBlock);
        Assert.Equal(2, Resolve(index, doc, new Ref(RefKind.Figure, "18"), 0)!.Value.TargetBlock);
    }

    [Fact]
    public void OneFloatTwoCaptions_SecondCaptionAbstains()
    {
        var doc = new FakeDoc(1).Add(0,
        [
            (BlockRole.Figure, 50, 100, 500, 200),
            (BlockRole.Caption, 50, 310, 500, 10),   // nearer
            (BlockRole.Caption, 50, 330, 500, 10),
        ],
        new() { [1] = "Figure 1: A.", [2] = "Figure 2: B." });
        var index = new ReferenceIndex();
        Assert.Equal(0, Resolve(index, doc, new Ref(RefKind.Figure, "1"), 0)!.Value.TargetBlock);
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Figure, "2"), 0));
    }

    // --- numbered statements ---

    private static FakeDoc WithStatement(FakeDoc doc, int page, string text)
        => doc.Add(page, [(BlockRole.Text, 50, 100, 500, 60)], new() { [0] = text });

    [Fact]
    public void Statement_ResolvesToTheBlockOpeningWithItsHeader()
    {
        var doc = WithStatement(new FakeDoc(4), 2, "Definition 3. (Trace) A trace is a finite sequence.");
        Assert.Equal(new ReferenceIndex.Target(2, 0, 0), Resolve(new(), doc, new Ref(RefKind.Definition, "3"), 3));
    }

    [Fact]
    public void Statement_HeaderMayStartMidBlock()
    {
        var doc = WithStatement(new FakeDoc(4), 1, "closing words of the paragraph before.\nTheorem 2. Let D be a deduction system.");
        Assert.Equal(1, Resolve(new(), doc, new Ref(RefKind.Theorem, "2"), 1)!.Value.Page);
    }

    [Fact]
    public void Statement_MentionInBodyTextIsNotAHeader()
    {
        var doc = WithStatement(new FakeDoc(4), 1, "Theorem 2 shows that the algorithm terminates.");
        Assert.Null(Resolve(new(), doc, new Ref(RefKind.Theorem, "2"), 1));
    }

    [Fact]
    public void Statement_HeaderRepeatedOnOnePageIsAmbiguousAndAbstains()
    {
        var doc = new FakeDoc(3).Add(1,
            [(BlockRole.Text, 50, 100, 500, 40), (BlockRole.Text, 50, 200, 500, 40)],
            new() { [0] = "Lemma 1. First statement.", [1] = "Lemma 1. Restated here." });
        Assert.Null(Resolve(new(), doc, new Ref(RefKind.Lemma, "1"), 1));
    }

    [Fact]
    public void Statement_RepeatedLabelAcrossPapersTakesTheNearest()
    {
        var doc = WithStatement(WithStatement(new FakeDoc(60), 3, "Definition 1. Old paper."), 40, "Definition 1. This paper.");
        Assert.Equal(40, Resolve(new(), doc, new Ref(RefKind.Definition, "1"), 44)!.Value.Page);
    }

    [Fact]
    public void Statement_KindsAreKeptApart()
    {
        var doc = WithStatement(new FakeDoc(3), 1, "Lemma 2. Something.");
        Assert.Null(Resolve(new(), doc, new Ref(RefKind.Theorem, "2"), 1));
    }

    [Fact]
    public void Statement_WrappedMentionAtLineStartIsNotAHeader()
    {
        // "…are given in" wraps, leaving "Remark 2.15. We will see…" at the start of the next line.
        var doc = WithStatement(new FakeDoc(4), 1,
            "Comments on Definition 2.14 are given in\nRemark 2.15. We will see in Section 3.2 that these");
        Assert.Null(Resolve(new(), doc, new Ref(RefKind.Remark, "2.15"), 1));
    }

    [Fact]
    public void Statement_HeaderAfterASentenceEndingLineIsAHeader()
    {
        var doc = WithStatement(new FakeDoc(4), 1, "which closes the previous paragraph.\nRemark 2.15. Let us analyse these.");
        Assert.Equal(1, Resolve(new(), doc, new Ref(RefKind.Remark, "2.15"), 1)!.Value.Page);
    }

    [Theory]
    [InlineData(BlockRole.Heading)]
    [InlineData(BlockRole.Caption)]
    public void Statement_HeaderTypedAsHeadingOrCaptionStillCounts(BlockRole role)
    {
        var doc = new FakeDoc(3).Add(1, [(role, 50, 100, 500, 20)], new() { [0] = "Definition 2.14 (derived operations)." });
        Assert.Equal(1, Resolve(new(), doc, new Ref(RefKind.Definition, "2.14"), 1)!.Value.Page);
    }

    [Fact]
    public void Scan_OwnHeaderDoesNotHideALaterMentionOnTheSamePage()
    {
        // The header line of Definition 4 parses as a "mention" of itself; the genuine mention in the
        // note after it must still be reported.
        var doc = new FakeDoc(2).Add(0,
            [(BlockRole.Text, 50, 100, 500, 40), (BlockRole.Text, 50, 200, 500, 40)],
            new() { [0] = "Definition 4. Let E be a finite set.", [1] = "Note that B can be empty in Definition 4. If so, done." });
        var results = ReferenceBenchmark.Scan(doc, new ReferenceIndex());
        var m = Assert.Single(results);
        Assert.Equal(("Definition", "4", "resolved"), (m.Kind, m.Number, m.Status));
        Assert.Equal(1, m.Block);
    }

    // --- statement extent (what the preview shows) ---

    private static FakeDoc ExtentPage(params (BlockRole Role, string? Text)[] blocks)
    {
        var geometry = blocks.Select((b, i) => (b.Role, 50f, 100f + i * 60f, 500f, 40f)).ToArray();
        var texts = new Dictionary<int, string>();
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i].Text is { } t) texts[i] = t;
        return new FakeDoc(1).Add(0, geometry, texts);
    }

    [Fact]
    public void StatementExtent_RunsOverContinuationBlocksUpToTheNextStatement()
    {
        var doc = ExtentPage(
            (BlockRole.Text, "Definition 3. Let D be a system such that"),
            (BlockRole.Text, "(i) D is a set of rules and"),
            (BlockRole.DisplayMath, "x = y"),
            (BlockRole.Text, "Definition 4. Let E be a set."));
        Assert.Equal(2, ReferenceIndex.StatementExtentEnd(doc, 0, 0));
    }

    [Theory]
    [InlineData(BlockRole.Heading, "2 Next section")]
    [InlineData(BlockRole.Figure, null)]
    [InlineData(BlockRole.Caption, "Figure 1: Something.")]
    [InlineData(BlockRole.Footer, "12")]
    public void StatementExtent_StopsAtHeadingsFloatsAndFurniture(BlockRole stopper, string? text)
    {
        var doc = ExtentPage((BlockRole.Text, "Theorem 1. If a then b."), (stopper, text));
        Assert.Equal(0, ReferenceIndex.StatementExtentEnd(doc, 0, 0));
    }

    [Fact]
    public void StatementExtent_StopsAtProof()
    {
        var doc = ExtentPage(
            (BlockRole.Text, "Lemma 2. Let x be given."),
            (BlockRole.Text, "so that y holds."),
            (BlockRole.Text, "Proof. By induction."));
        Assert.Equal(1, ReferenceIndex.StatementExtentEnd(doc, 0, 0));
    }

    [Fact]
    public void StatementExtent_IsCappedAndSurvivesAMissingPage()
    {
        var many = Enumerable.Range(0, 20).Select(_ => (BlockRole.Text, (string?)"continued text of the statement")).ToList();
        many.Insert(0, (BlockRole.Text, "Theorem 1. Long statement."));
        Assert.Equal(8, ReferenceIndex.StatementExtentEnd(ExtentPage([.. many]), 0, 0));
        Assert.Equal(3, ReferenceIndex.StatementExtentEnd(new FakeDoc(1), 5, 3));   // page not analysed
    }

    // --- display equations ---

    private static FakeDoc WithDisplay(FakeDoc doc, int page, params string[] displays)
        => doc.Add(page,
            [.. displays.Select((_, i) => (BlockRole.DisplayMath, 50f, 100f + i * 60f, 500f, 40f))],
            displays.Select((d, i) => (d, i)).ToDictionary(x => x.i, x => x.d));

    [Fact]
    public void Equation_ResolvesToTheDisplayBlockCarryingItsTag()
        => Assert.Equal(new ReferenceIndex.Target(2, 1, 1),
            Resolve(new(), WithDisplay(new FakeDoc(4), 2, "a = b (3.1)", "x = y + z (3.2)"), new Ref(RefKind.Equation, "3.2"), 3));

    [Fact]
    public void Equation_TagAtTheLeftMarginCounts()
        => Assert.Equal(1, Resolve(new(), WithDisplay(new FakeDoc(4), 1, "(1) y = X b + u"), new Ref(RefKind.Equation, "1"), 2)!.Value.Page);

    [Fact]
    public void Equation_TheSameNumberTwiceOnAPageIsAmbiguous()
        => Assert.Null(Resolve(new(), WithDisplay(new FakeDoc(4), 1, "a = b (4)", "c = d (4)"), new Ref(RefKind.Equation, "4"), 1));

    [Theory]
    [InlineData("f(0) = 1")]            // a function argument is attached to its function
    [InlineData("R2 = 0.52 (0.52)")]    // a decimal value
    [InlineData("see Smith (2019)")]    // a year
    public void Equation_NonTagsInADisplayAreNotLabels(string display)
    {
        var doc = WithDisplay(new FakeDoc(3), 1, display);
        foreach (string n in new[] { "0", "0.52", "2019" })
            Assert.Null(Resolve(new(), doc, new Ref(RefKind.Equation, n), 1));
    }

    [Fact]
    public void Equation_FlatNumberBeyondTheCapAbstainsButADottedOneDoesNot()
    {
        var doc = WithDisplay(WithDisplay(new FakeDoc(60), 2, "a = b (5)"), 3, "c = d (5.1)");
        var index = new ReferenceIndex { MaxFlatNumberPageDistance = 12 };
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Equation, "5"), 40));
        Assert.Equal(3, Resolve(index, doc, new Ref(RefKind.Equation, "5.1"), 40)!.Value.Page);
    }

    [Fact]
    public void Scan_AnEquationTagIsNotAMentionOfItself()
    {
        var doc = WithDisplay(new FakeDoc(2), 0, "a = b (3.1)");
        Assert.Empty(ReferenceBenchmark.Scan(doc, new ReferenceIndex()));
    }

    [Fact]
    public void Equation_MentionMergedIntoADisplayIsNotATag()
    {
        var doc = WithDisplay(new FakeDoc(2), 0, "Thus (C.1) and (C.2) give x = y (C.3)");
        var index = new ReferenceIndex();
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Equation, "c.1"), 0));
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Equation, "c.2"), 0));
        Assert.Equal(0, Resolve(index, doc, new Ref(RefKind.Equation, "c.3"), 0)!.Value.Page);
    }

    [Fact]
    public void Equation_DefinedTwiceWithinReachAbstains_ButAFarCopyDoesNot()
    {
        var doc = WithDisplay(WithDisplay(WithDisplay(new FakeDoc(80), 10, "a = b (7)"), 14, "c = d (7)"), 70, "e = f (7)");
        var index = new ReferenceIndex { MaxFlatNumberPageDistance = 12 };
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Equation, "7"), 12));            // two inside the window
        Assert.Equal(70, Resolve(index, doc, new Ref(RefKind.Equation, "7"), 72)!.Value.Page);   // only one in reach
    }

    [Fact]
    public void Figure_RestartingLabelStillTakesTheNearest()
    {
        var doc = new FakeDoc(30).AddFigure(10, "Figure 1: A.").AddFigure(14, "Figure 1: B.");
        Assert.Equal(14, Resolve(new(), doc, new Ref(RefKind.Figure, "1"), 15)!.Value.Page);
    }

    // --- adaptive flat-number cap ---

    [Fact]
    public void AdaptiveCap_ADocumentWhoseLabelsNeverRestartResolvesAtAnyDistance()
    {
        var doc = new FakeDoc(60).AddFigure(2, "Figure 4: Mine.").AddFigure(5, "Figure 5: Other.").Complete();
        var index = new ReferenceIndex { MaxFlatNumberPageDistance = 12 };
        Assert.Equal(2, Resolve(index, doc, new Ref(RefKind.Figure, "4"), 50)!.Value.Page);
    }

    [Fact]
    public void AdaptiveCap_ARestartingDocumentKeepsTheCap()
    {
        // "Figure 1" is defined on pages 2 and 30: a proceedings volume. "Figure 4" exists once (page 2),
        // but a mention on page 40 belongs to whichever paper it sits in.
        var doc = new FakeDoc(60).AddFigure(2, "Figure 1: A.").AddFigure(3, "Figure 4: B.").AddFigure(30, "Figure 1: C.").Complete();
        var index = new ReferenceIndex { MaxFlatNumberPageDistance = 12 };
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Figure, "4"), 40));
        Assert.Equal(3, Resolve(index, doc, new Ref(RefKind.Figure, "4"), 10)!.Value.Page);   // inside the window
    }

    [Fact]
    public void AdaptiveCap_AnUnfinishedAnalysisKeepsTheCap()
    {
        var doc = new FakeDoc(60).AddFigure(2, "Figure 4: Mine.");   // 1 of 60 pages analysed
        Assert.Null(Resolve(new ReferenceIndex { MaxFlatNumberPageDistance = 12 }, doc, new Ref(RefKind.Figure, "4"), 50));
    }

    [Fact]
    public void AdaptiveCap_CanBeSwitchedOff()
    {
        var doc = new FakeDoc(60).AddFigure(2, "Figure 4: Mine.").Complete();
        var index = new ReferenceIndex { MaxFlatNumberPageDistance = 12, AdaptiveCap = false };
        Assert.Null(Resolve(index, doc, new Ref(RefKind.Figure, "4"), 50));
    }

    [Fact]
    public void AdaptiveCap_AUniqueEquationFarAwayResolves_AnEquationDefinedTwiceFarApartDoesNot()
    {
        var unique = WithDisplay(new FakeDoc(80), 5, "a = b (7)").Complete();
        Assert.Equal(5, Resolve(new ReferenceIndex(), unique, new Ref(RefKind.Equation, "7"), 70)!.Value.Page);

        var twice = WithDisplay(WithDisplay(new FakeDoc(80), 5, "a = b (7)"), 60, "c = d (7)").Complete();
        Assert.Equal(60, Resolve(new ReferenceIndex(), twice, new Ref(RefKind.Equation, "7"), 62)!.Value.Page);   // inside the window
        Assert.Null(Resolve(new ReferenceIndex(), twice, new Ref(RefKind.Equation, "7"), 35));                    // far from both
    }

    [Fact]
    public void AdaptiveCap_ScopedNumbersWereNeverCapped()
    {
        var doc = new FakeDoc(80).AddFigure(2, "Figure 4.1: Chapter figure.").AddFigure(30, "Figure 1: A.").AddFigure(60, "Figure 1: B.").Complete();
        Assert.Equal(2, Resolve(new ReferenceIndex(), doc, new Ref(RefKind.Figure, "4.1"), 75)!.Value.Page);
    }

    [Fact]
    public void AdaptiveCap_AnEquationDefinedTwiceCloseTogetherIsAmbiguousFromFarAway()
    {
        // (7) on pages 3 and 5: no restart evidence (they are within the cap of each other), so a
        // mention on page 40 sees both and abstains instead of taking the nearer.
        var doc = WithDisplay(WithDisplay(new FakeDoc(60), 3, "a = b (7)"), 5, "c = d (7)").Complete();
        Assert.Null(Resolve(new ReferenceIndex(), doc, new Ref(RefKind.Equation, "7"), 40));
        Assert.Null(Resolve(new ReferenceIndex(), doc, new Ref(RefKind.Equation, "7"), 8));
    }
}
