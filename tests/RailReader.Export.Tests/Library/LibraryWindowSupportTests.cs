using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RailReader.Renderer.Skia;
using RailReader2.Library;
using SkiaSharp;
using Xunit;

namespace RailReader.Export.Tests.Library;

/// <summary>The library pieces behind the Library window and Settings ▸ Library (M7 Phase 2).</summary>
public sealed class LibraryWindowSupportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_libwin_").FullName;
    private string Db => Path.Combine(_dir, "library.db");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static LibraryHit Hit(string doc, int? page, double score, HitSource source = HitSource.Page,
        string? path = null) => new(doc, path ?? $"/papers/{doc}.pdf", $"{doc}.pdf", page, source, "s", score);

    private static DocumentContent Doc(string id, string file, params string[] pages) => new(
        id, $"/papers/{file}", 100, 200, pages.Length, false,
        pages.Select((b, i) => new PageEntry(i, b, false)).ToList(), [], []);

    // ------------------------------------------------------------ grouping and labels

    [Fact]
    public void Group_OrdersDocumentsByBestHit_AndPrefersDifferentPages()
    {
        var hits = new[]
        {
            Hit("a", 1, 5), Hit("b", 0, 9), Hit("a", 1, 4), Hit("a", 2, 3), Hit("a", 1, 2), Hit("a", 7, 1),
        };
        var groups = LibraryResults.Group(hits, perDocument: 3, exists: _ => true);
        Assert.Equal(["b", "a"], groups.Select(g => g.ContentId));
        var a = groups[1];
        Assert.Equal(5, a.HitCount);
        Assert.Equal([1, 2, 7], a.Hits.Select(h => h.Page!.Value)); // three different pages before a repeat
        Assert.False(a.Missing);
    }

    [Fact]
    public void Group_MarksMissingFiles_AndCountsNotes()
    {
        var groups = LibraryResults.Group([Hit("a", 0, 1), Hit("b", 0, 1, path: "")],
            exists: p => p != "/papers/a.pdf", noteCounts: new Dictionary<string, int> { ["a"] = 4 });
        Assert.True(groups.Single(g => g.ContentId == "a").Missing);
        Assert.True(groups.Single(g => g.ContentId == "b").Missing); // no path at all
        Assert.Equal(4, groups.Single(g => g.ContentId == "a").NoteCount);
    }

    [Fact]
    public void Label_NamesTheSource_AndTellsReviewersFromTheReader()
    {
        var note = Hit("a", 3, 1, HitSource.Note) with { NoteKind = "highlight", NoteOrigin = NoteSource.InPdf, Author = "Referee 2", Category = "Definition" };
        Assert.Equal("page 4 · reviewer comment (Referee 2) · Definition", LibraryResults.Label(note, "Stefan"));
        Assert.Equal("page 4 · your note · Definition", LibraryResults.Label(note with { Author = "stefan " }, "Stefan"));
        Assert.Equal("page 4 · your note", LibraryResults.Label(note with { NoteOrigin = NoteSource.RailReader, Category = null }, null));
        Assert.Equal("page 4 · reviewer comment", LibraryResults.Label(note with { Author = null, Category = null }, "Stefan"));
        Assert.Equal("page 1 · OCR text, unverified", LibraryResults.Label(Hit("a", 0, 1) with { FromOcr = true }, null));
        Assert.Equal("page 2 · outline", LibraryResults.Label(Hit("a", 1, 1, HitSource.Outline), null));
        Assert.Equal("file name", LibraryResults.Label(Hit("a", null, 1, HitSource.Name), null));
    }

    // ------------------------------------------------------------ highlight pattern

    [Theory]
    [InlineData("estimators", "the estimation of", true)]
    [InlineData("estimators", "nonestimators", false)]   // a word start, not inside a word
    [InlineData("\"confidence band\"", "the confidence\nband is", true)]
    [InlineData("\"confidence band\"", "confidence-band", true)]
    [InlineData("contrast*", "contrastive learning", true)]
    [InlineData("KERNEL", "a kernel trick", true)]
    [InlineData("ridge -lasso", "lasso", false)]           // excluded words are not highlighted
    [InlineData("a.b", "aXb", false)]                      // regex characters are literal
    public void HighlightPattern_MatchesTheQueryWordsInPageText(string query, string text, bool matches)
    {
        var pattern = LibraryQuery.Parse(query).HighlightPattern;
        Assert.NotNull(pattern);
        Assert.Equal(matches, Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)); // as the Search pane runs it
    }

    [Fact]
    public void HighlightPattern_IsNullWithoutTerms() => Assert.Null(LibraryQuery.Parse("-only -excluded").HighlightPattern);

    // ------------------------------------------------------------ index: filters, counts, clear

    [Fact]
    public void Search_LimitsToTheChosenSources_AndReturnsCategories()
    {
        using var index = LibraryIndex.OpenWritable(Db, out _);
        index.ReplaceDocument(Doc("a", "bootstrap.pdf", "the bootstrap method") with
        {
            Outline = [new OutlineItem("Bootstrap bands", 0)],
            Notes = [new NoteEntry(0, "highlight", null, "bootstrap again", null, "#FFFF00", null, NoteSource.RailReader)],
        }, color => color == "#FFFF00" ? "Definition" : null);
        index.ReplaceDocument(Doc("b", "plain.pdf", "nothing"));

        var q = LibraryQuery.Parse("bootstrap");
        Assert.Equal(4, index.Search(q).Count);
        Assert.Equal([HitSource.Note], index.Search(q, sources: new HashSet<HitSource> { HitSource.Note }).Select(h => h.Source));
        Assert.Equal("Definition", Assert.Single(index.Search(q, sources: new HashSet<HitSource> { HitSource.Note })).Category);
        Assert.Empty(index.Search(q, sources: new HashSet<HitSource>()));
        Assert.Equal(["Definition"], index.Categories());
        Assert.Equal(new Dictionary<string, int> { ["a"] = 1 }, index.NoteCounts());
    }

    [Fact]
    public void Clear_EmptiesTheIndex_AndControlsTheBackfill()
    {
        using var index = LibraryIndex.OpenWritable(Db, out _);
        index.ReplaceDocument(Doc("a", "a.pdf", "words"));
        Assert.True(index.BackfillEnabled);
        index.Clear(backfill: false);
        Assert.Equal(0, index.GetStatus().Documents);
        Assert.Empty(index.Search(LibraryQuery.Parse("words")));
        Assert.False(index.BackfillEnabled);
        index.Clear(backfill: true);
        Assert.True(index.BackfillEnabled);
    }

    [Fact]
    public void SetPath_PointsTheEntryAtTheLocatedFile()
    {
        using var index = LibraryIndex.OpenWritable(Db, out _);
        index.ReplaceDocument(Doc("a", "old.pdf", "words"));
        index.SetPath("a", "/elsewhere/new name.pdf");
        var hit = Assert.Single(index.Search(LibraryQuery.Parse("words")));
        Assert.Equal(("/elsewhere/new name.pdf", "new name.pdf"), (hit.LastPath, hit.FileName));
    }

    // ------------------------------------------------------------ writer jobs

    private string MakePdf(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        using var doc = SKDocument.CreatePdf(path);
        using var font = new SKFont { Size = 24 };
        using var paint = new SKPaint { Color = SKColors.Black };
        using (var canvas = doc.BeginPage(612, 792))
            canvas.DrawText(text, 72, 100, SKTextAlign.Left, font, paint);
        doc.EndPage();
        doc.Close();
        return path;
    }

    private IReadOnlyList<LibraryHit> Search(string q)
    {
        using var index = LibraryIndex.OpenReadOnly(Db)!;
        return index.Search(LibraryQuery.Parse(q));
    }

    [Fact]
    public void Writer_RemoveRelocateClear_AndRaisesChanged()
    {
        var pdf = MakePdf("paper.pdf", "spectral clustering");
        int changes = 0;
        using var w = new LibraryWriter(Db, new SkiaPdfServiceFactory());
        w.Changed += () => Interlocked.Increment(ref changes);

        w.DocumentOpened(pdf, "id1", false, null, 0);
        Assert.True(w.WaitForForeground(Wait));
        Assert.True(Volatile.Read(ref changes) > 0);

        // Moved: the entry follows the located file (and re-reads it, its size/mtime having changed).
        var moved = Path.Combine(_dir, "moved.pdf");
        File.Move(pdf, moved);
        File.AppendAllText(moved, "\n");
        w.Relocate("id1", moved);
        Assert.True(w.WaitForForeground(Wait));
        Assert.Equal(moved, Assert.Single(Search("spectral")).LastPath);

        w.Remove("id1");
        Assert.True(w.WaitForForeground(Wait));
        Assert.Empty(Search("spectral"));

        w.DocumentOpened(moved, "id1", false, null, 0);
        w.Clear();
        Assert.True(w.WaitForForeground(Wait));
        using var index = LibraryIndex.OpenReadOnly(Db)!;
        Assert.Equal(0, index.GetStatus().Documents);
        Assert.False(index.BackfillEnabled);
    }

    [Fact]
    public void Removed_StaysOutUntilOpened_AndClearForgetsTheMarker()
    {
        using (var index = LibraryIndex.OpenWritable(Db, out _))
        {
            index.ReplaceDocument(Doc("a", "a.pdf", "words"));
            Assert.False(index.IsRemoved("a"));
            index.Remove("a");
            Assert.True(index.IsRemoved("a")); // the backfill checks this and skips the document
            index.Clear(backfill: true);
            Assert.False(index.IsRemoved("a"));
            index.ReplaceDocument(Doc("a", "a.pdf", "words"));
            index.Remove("a");
        }
        SqliteConnection.ClearAllPools();

        // Opening the document again brings it back.
        var pdf = MakePdf("a.pdf", "spectral words");
        using (var w = new LibraryWriter(Db, new SkiaPdfServiceFactory()))
        {
            w.DocumentOpened(pdf, "a", false, null, 0);
            Assert.True(w.WaitForForeground(Wait));
        }
        using var reopened = LibraryIndex.OpenWritable(Db, out _);
        Assert.False(reopened.IsRemoved("a"));
        Assert.Single(reopened.Search(LibraryQuery.Parse("spectral")));
    }

    [Fact]
    public void Writer_ReportsBackfillProgress_ThenResets()
    {
        using var w = new LibraryWriter(Db, new SkiaPdfServiceFactory()) { BusyRecheck = TimeSpan.FromMilliseconds(50) };
        w.IsBusy = true;
        w.Backfill([Path.Combine(_dir, "gone1.pdf"), Path.Combine(_dir, "gone2.pdf")]);
        Assert.Equal((0, 2), w.BackfillProgress);
        w.IsBusy = false;
        var deadline = DateTime.UtcNow + Wait;
        while (w.BackfillRemaining > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Thread.Sleep(50);
        Assert.Equal((0, 0), w.BackfillProgress);
    }
}
