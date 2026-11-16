using Microsoft.Data.Sqlite;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.Library;
using SkiaSharp;
using Xunit;

namespace RailReader.Export.Tests.Library;

public sealed class LibraryIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_library_").FullName;
    private string Db => Path.Combine(_dir, "library.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DocumentContent Doc(string id, string file, params string[] pages) => new(
        id, $"/papers/{file}", 100, 200, pages.Length, false,
        pages.Select((b, i) => new PageEntry(i, b, false)).ToList(), [], []);

    private LibraryIndex Open() => LibraryIndex.OpenWritable(Db, out _);

    [Fact]
    public void Search_FindsStemmedAndDiacriticFoldedWords_OnTheRightPage()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "nothing here", "Consistent estimators of the café variance"));
        var hits = index.Search(LibraryQuery.Parse("estimation cafe"));
        var hit = Assert.Single(hits);
        Assert.Equal(("a", 1, HitSource.Page), (hit.ContentId, hit.Page, hit.Source));
        Assert.Contains("[estimators]", hit.Snippet);
    }

    [Fact]
    public void Search_RanksTheDenserPageFirst()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "bootstrap once among many many other words about regression and models"));
        index.ReplaceDocument(Doc("b", "b.pdf", "bootstrap bootstrap bootstrap band"));
        Assert.Equal(["b", "a"], index.Search(LibraryQuery.Parse("bootstrap")).Select(h => h.ContentId));
    }

    [Fact]
    public void Search_PhraseAndExclusion()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "the confidence band is wide"));
        index.ReplaceDocument(Doc("b", "b.pdf", "a band of confidence"));
        index.ReplaceDocument(Doc("c", "c.pdf", "confidence band with lasso"));
        Assert.Equal(["a"], index.Search(LibraryQuery.Parse("\"confidence band\" -lasso")).Select(h => h.ContentId));
    }

    [Fact]
    public void Search_HostileInputDoesNotThrow()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "C++ and NEAR and AND"));
        foreach (var q in new[] { "NEAR/5", "a OR", "(((", "\"", "col:umn", "^start", "C++" })
            _ = index.Search(LibraryQuery.Parse(q));
    }

    [Fact]
    public void ReplaceDocument_ReplacesRatherThanAppends()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "old words"));
        index.ReplaceDocument(Doc("a", "moved.pdf", "new words"));
        Assert.Empty(index.Search(LibraryQuery.Parse("old")));
        var hit = Assert.Single(index.Search(LibraryQuery.Parse("new")));
        Assert.Equal("moved.pdf", hit.FileName);
        Assert.Equal(1, index.GetStatus().Documents);
    }

    [Fact]
    public void Outline_Notes_And_FileName_AreSearchable_AndLabelled()
    {
        using var index = Open();
        var doc = Doc("a", "Breusch-SimpleTest_1979.pdf", "page text") with
        {
            Outline = [new OutlineItem("4.2 Identification", 3)],
            Notes =
            [
                new NoteEntry(2, "highlight", "the marked words", "check this proof", "Referee 2", "#FFFF00", "nm1", NoteSource.InPdf),
                new NoteEntry(5, "bookmark", null, "wild bootstrap", null, null, null, NoteSource.RailReader),
            ],
        };
        index.ReplaceDocument(doc);

        Assert.Equal(HitSource.Outline, Assert.Single(index.Search(LibraryQuery.Parse("identification"))).Source);
        var note = Assert.Single(index.Search(LibraryQuery.Parse("proof")));
        Assert.Equal((HitSource.Note, 2, "highlight", "Referee 2", NoteSource.InPdf), (note.Source, note.Page, note.NoteKind, note.Author, note.NoteOrigin));
        Assert.Equal(HitSource.Note, Assert.Single(index.Search(LibraryQuery.Parse("marked"))).Source);
        Assert.Equal("bookmark", Assert.Single(index.Search(LibraryQuery.Parse("wild"))).NoteKind);
        var name = Assert.Single(index.Search(LibraryQuery.Parse("breusch simple")));
        Assert.Equal((HitSource.Name, (int?)null), (name.Source, name.Page));
    }

    [Fact]
    public void ReplaceNotes_And_SetCategory()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "text"));
        index.ReplaceNotes("a", [new NoteEntry(0, "highlight", null, "first", null, "#FFFF00", null, NoteSource.RailReader)]);
        index.ReplaceNotes("a", [new NoteEntry(0, "highlight", null, "second", null, "#ffff00", null, NoteSource.RailReader)]);
        Assert.Empty(index.Search(LibraryQuery.Parse("first")));
        Assert.Single(index.Search(LibraryQuery.Parse("second")));

        index.SetCategory("#FFFF00", "Definition");
        Assert.Single(index.Search(LibraryQuery.Parse("definition")));
        index.SetCategory("#FFFF00", null);
        Assert.Empty(index.Search(LibraryQuery.Parse("definition")));

        index.ReplaceNotes("unknown", [new NoteEntry(0, "textnote", null, "x", null, null, null, NoteSource.RailReader)]); // no-op
    }

    [Fact]
    public void AddOcrPages_FillsOnlyPagesWithoutText()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "native words", ""));
        int added = index.AddOcrPages("a", [new PageEntry(0, "ocr replacement", true), new PageEntry(1, "scanned words", true)]);
        Assert.Equal(1, added);
        Assert.Empty(index.Search(LibraryQuery.Parse("replacement")));
        var hit = Assert.Single(index.Search(LibraryQuery.Parse("scanned")));
        Assert.True(hit.FromOcr);
        Assert.Equal(1, hit.Page);
    }

    [Fact]
    public void NeedsIndex_TracksFileChangesAndIndexerVersion()
    {
        var pdf = Path.Combine(_dir, "x.pdf");
        File.WriteAllText(pdf, "12345");
        var fi = new FileInfo(pdf);
        using var index = Open();
        Assert.True(index.NeedsIndex("x", pdf));
        index.ReplaceDocument(Doc("x", "x.pdf", "t") with { Path = pdf, FileSize = fi.Length, FileMtimeTicks = fi.LastWriteTimeUtc.Ticks });
        Assert.False(index.NeedsIndex("x", pdf));
        File.AppendAllText(pdf, "6");
        Assert.True(index.NeedsIndex("x", pdf));
    }

    [Fact]
    public void RecordOpen_And_Remove()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "a.pdf", "words"));
        var when = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        index.RecordOpen("a", "/elsewhere/renamed.pdf", 7, when);
        var d = index.GetDocument("a")!;
        Assert.Equal(("/elsewhere/renamed.pdf", "renamed.pdf", 7, when), (d.LastPath, d.FileName, d.LastPage, d.LastOpenedUtc?.ToUniversalTime()));
        Assert.True(index.Remove("a"));
        Assert.Empty(index.Search(LibraryQuery.Parse("words")));
        Assert.Null(index.GetDocument("a"));
    }

    [Fact]
    public void ReadOnly_SeesWrites_AndRefusesToWrite()
    {
        using var writer = Open();
        writer.ReplaceDocument(Doc("a", "a.pdf", "shared words"));
        using var reader = LibraryIndex.OpenReadOnly(Db)!;
        Assert.Single(reader.Search(LibraryQuery.Parse("shared")));
        Assert.Throws<InvalidOperationException>(() => reader.Remove("a"));
        Assert.Null(LibraryIndex.OpenReadOnly(Path.Combine(_dir, "missing.db")));
    }

    [Fact]
    public void Unreadable_IsMovedAsideAndRebuilt()
    {
        File.WriteAllText(Db, "this is not a database, just text long enough to look like a file header....");
        using (var index = LibraryIndex.OpenWritable(Db, out bool recovered))
        {
            Assert.True(recovered);
            index.ReplaceDocument(Doc("a", "a.pdf", "fresh"));
        }
        Assert.True(File.Exists(Db + ".unreadable"));
        Assert.Null(LibraryIndex.OpenReadOnly(Db + ".unreadable"));
    }

    [Fact]
    public void NewerSchema_IsUnreadable()
    {
        using (var index = Open()) { }
        using (var c = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "update meta set value = '99' where key = 'schema_version'";
            cmd.ExecuteNonQuery();
        }
        Assert.Null(LibraryIndex.OpenReadOnly(Db));
        using var reopened = LibraryIndex.OpenWritable(Db, out bool recovered);
        Assert.True(recovered);
    }

    [Theory]
    [InlineData("Breusch-SimpleTestHeteroscedasticity-1979.pdf", "Breusch Simple Test Heteroscedasticity 1979")]
    [InlineData("CRT zhang.pdf", "CRT zhang")]
    [InlineData("svd_ols_ridge_pcr.pdf", "svd ols ridge pcr")]
    public void NameTerms(string file, string expected) => Assert.Equal(expected, LibraryIndex.NameTerms(file));

    [Fact]
    public void Indexer_ExtractsARealPdf_AndLocksNothingUnencrypted()
    {
        var path = Path.Combine(_dir, "real.pdf");
        using (var doc = SKDocument.CreatePdf(path))
        {
            using var font = new SKFont { Size = 24 };
            using var paint = new SKPaint { Color = SKColors.Black };
            foreach (var line in new[] { "First page about kernels", "Second page about heteroscedasticity" })
            {
                using (var canvas = doc.BeginPage(612, 792))
                    canvas.DrawText(line, 72, 100, SKTextAlign.Left, font, paint);
                doc.EndPage();
            }
            doc.Close();
        }
        var annotations = new AnnotationFile();
        annotations.Pages[1] = [new TextNoteAnnotation { Text = "remember this", Contents = "remember this" }];
        annotations.Bookmarks.Add(new BookmarkEntry { Name = "start here", Page = 0 });

        var content = LibraryIndexer.Extract(path, "real", new SkiaPdfServiceFactory(), annotations);
        Assert.Equal((DocumentState.Indexed, 2, false), (content.State, content.PageCount, content.Encrypted));
        Assert.Contains("heteroscedasticity", content.Pages[1].Body);
        Assert.Equal(["textnote", "bookmark"], content.Notes.Select(n => n.Kind));

        using var index = Open();
        index.ReplaceDocument(content);
        Assert.Equal(1, Assert.Single(index.Search(LibraryQuery.Parse("heteroscedasticity"))).Page);
        Assert.Equal(1, Assert.Single(index.Search(LibraryQuery.Parse("remember"))).Page);
    }

    [Fact]
    public void Indexer_MissingFile_IsFailed()
    {
        var content = LibraryIndexer.Extract(Path.Combine(_dir, "nope.pdf"), "nope", new SkiaPdfServiceFactory());
        Assert.Equal(DocumentState.Failed, content.State);
        Assert.Empty(content.Pages);
    }

    [Fact]
    public void KnownDocuments_ReadsTheMapTolerantly()
    {
        var map = Path.Combine(_dir, "document_ids.json");
        File.WriteAllText(map, """{"version":1,"files":[["/a.pdf",{"id":"x"}],["/b.pdf",{}],[42],"junk"],"aliases":[]}""");
        Assert.Equal(["/a.pdf", "/b.pdf"], KnownDocuments.FromIdentityMap(map));
        File.WriteAllText(map, "{ not json");
        Assert.Empty(KnownDocuments.FromIdentityMap(map));
        Assert.Empty(KnownDocuments.FromIdentityMap(Path.Combine(_dir, "none.json")));
    }
}
