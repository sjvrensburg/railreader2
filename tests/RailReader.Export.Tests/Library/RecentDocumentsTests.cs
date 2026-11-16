using Microsoft.Data.Sqlite;
using RailReader.Renderer.Skia;
using RailReader2.Library;
using Xunit;

namespace RailReader.Export.Tests.Library;

/// <summary>The welcome-screen dashboard's data (M7 Phase 3).</summary>
public sealed class RecentDocumentsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_recents_").FullName;
    private string Db => Path.Combine(_dir, "library.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DocumentContent Doc(string id, string path, int pages, DocumentState state = DocumentState.Indexed) => new(
        id, path, 1, 1, pages, state == DocumentState.Locked,
        Enumerable.Range(0, pages).Select(i => new PageEntry(i, "text", false)).ToList(), [], [], state);

    [Fact]
    public void Build_DeduplicatesByContentId_JoinsTheIndex_AndKeepsTheNewestEntry()
    {
        using var index = LibraryIndex.OpenWritable(Db, out _);
        index.ReplaceDocument(Doc("A", "/b/paper.pdf", 230) with
        {
            Notes = [new NoteEntry(0, "textnote", null, "n1", null, null, null, NoteSource.RailReader),
                     new NoteEntry(1, "textnote", null, "n2", null, null, null, NoteSource.RailReader)],
        });
        index.ReplaceDocument(Doc("S", "/b/secret.pdf", 0, DocumentState.Locked));
        var ids = new Dictionary<string, string> { ["/a/paper.pdf"] = "A", ["/b/paper.pdf"] = "A", ["/b/secret.pdf"] = "S" };

        var cards = RecentDocuments.Build(
            [("/a/paper.pdf", 40), ("/b/paper.pdf", 3), ("/b/secret.pdf", 0), ("/c/unknown.pdf", 2), ("", 0)],
            p => ids.GetValueOrDefault(p), index, exists: p => p != "/c/unknown.pdf");

        Assert.Equal(["/a/paper.pdf", "/b/secret.pdf", "/c/unknown.pdf"], cards.Select(c => c.Path));
        var paper = cards[0];
        Assert.Equal(("paper", "page 41 of 230", 2, false), (paper.Title, paper.Progress, paper.NoteCount, paper.Missing));
        Assert.Equal(41.0 / 230, paper.Fraction!.Value, 6);
        Assert.True(cards[1].Locked);
        Assert.Equal(("page 3", (double?)null, true), (cards[2].Progress, cards[2].Fraction, cards[2].Missing));
    }

    [Fact]
    public void Build_WorksWithoutAnIndex_AndCapsTheCount()
    {
        var recents = Enumerable.Range(0, 20).Select(i => ($"/p/{i}.pdf", 0));
        var cards = RecentDocuments.Build(recents, _ => null, index: null, exists: _ => true);
        Assert.Equal(RecentDocuments.MaxCards, cards.Count);
        Assert.All(cards, c => Assert.Null(c.PageCount));
    }

    [Fact]
    public void Build_SurvivesAThrowingIdentityLookup()
    {
        var cards = RecentDocuments.Build([("/p/a.pdf", 0)], _ => throw new IOException("map busy"), null, _ => true);
        Assert.Null(Assert.Single(cards).ContentId);
    }

    [Fact]
    public void ThumbnailIsCurrent_ComparesWithThePdf()
    {
        var pdf = Path.Combine(_dir, "a.pdf");
        var thumb = Path.Combine(_dir, "a.png");
        File.WriteAllText(pdf, "x");
        Assert.False(RecentDocuments.ThumbnailIsCurrent(thumb, pdf));
        File.WriteAllText(thumb, "png");
        File.SetLastWriteTimeUtc(thumb, File.GetLastWriteTimeUtc(pdf).AddMinutes(1));
        Assert.True(RecentDocuments.ThumbnailIsCurrent(thumb, pdf));
        File.SetLastWriteTimeUtc(pdf, File.GetLastWriteTimeUtc(thumb).AddMinutes(1)); // annotations written back
        Assert.False(RecentDocuments.ThumbnailIsCurrent(thumb, pdf));
    }

    [Fact]
    public void Writer_RemoveAndClear_DeleteThumbnails()
    {
        var thumbs = RecentDocuments.ThumbnailDirectory(Db);
        Directory.CreateDirectory(thumbs);
        File.WriteAllText(RecentDocuments.ThumbnailPath("a", Db), "png");
        File.WriteAllText(RecentDocuments.ThumbnailPath("b", Db), "png");
        using var w = new LibraryWriter(Db, new SkiaPdfServiceFactory());
        w.Remove("a");
        Assert.True(w.WaitForForeground(TimeSpan.FromSeconds(30)));
        Assert.False(File.Exists(RecentDocuments.ThumbnailPath("a", Db)));
        Assert.True(File.Exists(RecentDocuments.ThumbnailPath("b", Db)));
        w.Clear();
        Assert.True(w.WaitForForeground(TimeSpan.FromSeconds(30)));
        Assert.False(Directory.Exists(thumbs));
    }
}
