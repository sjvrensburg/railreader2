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
                var b = blocks[index];
                float cw = b.W / caption.Length;
                for (int i = 0; i < caption.Length; i++)
                    boxes.Add(new CharBox(text.Length + i, b.X + i * cw, b.Y, b.X + (i + 1) * cw, b.Y + b.H));
                text.Append(caption);
            }
            _pages[page] = (analysis, new PageText(text.ToString(), boxes));
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
}
