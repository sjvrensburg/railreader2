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
    public void RecordOpen_FromARenamedFile_ReindexesTheName()
    {
        using var index = Open();
        index.ReplaceDocument(Doc("a", "draft.pdf", "some text"));
        index.RecordOpen("a", "/papers/Smith-Regression-2020.pdf", 0, DateTime.UtcNow);
        var names = new HashSet<HitSource> { HitSource.Name };
        Assert.Single(index.Search(LibraryQuery.Parse("regression"), sources: names));
        Assert.Empty(index.Search(LibraryQuery.Parse("draft"), sources: names));
        index.SetPath("a", "/elsewhere/Renamed.pdf");
        Assert.Single(index.Search(LibraryQuery.Parse("renamed"), sources: names));
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

    [Fact]
    public void OlderSchema_IsRebuiltInPlace_NotMovedAside()
    {
        // A schema-1 index (rows filtered by an unindexed doc_key) predates the rowid layout.
        using (var c = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                create table meta (key text primary key, value text);
                insert into meta values ('schema_version', '1');
                create table documents (doc_key integer primary key, content_id text);
                create virtual table page_fts using fts5(body, doc_key unindexed);
                insert into page_fts(body, doc_key) values ('stale words', 1);
                """;
            cmd.ExecuteNonQuery();
        }
        using var index = LibraryIndex.OpenWritable(Db, out bool recovered);
        Assert.False(recovered);
        Assert.False(File.Exists(Db + ".unreadable"));
        Assert.Empty(index.Search(LibraryQuery.Parse("stale")));
        index.ReplaceDocument(Doc("a", "a.pdf", "fresh words"));
        Assert.Single(index.Search(LibraryQuery.Parse("fresh")));
    }

    [Fact]
    public void Rewriting_OneDocument_LeavesItsNeighboursAlone()
    {
        // Rows are keyed by rowid ranges per document; a range off by one would eat a neighbour's rows.
        using var index = Open();
        foreach (var id in new[] { "a", "b", "c" })
            index.ReplaceDocument(Doc(id, $"{id}.pdf", $"first {id}words", $"second {id}words") with
            {
                Outline = [new OutlineItem($"chapter {id}words", 0)],
                Notes = [new NoteEntry(0, "textnote", null, $"note {id}words", null, null, null, NoteSource.RailReader)],
            });
        index.ReplaceDocument(Doc("b", "b.pdf", "replaced bwords"));
        index.Remove("a");
        index.ReplaceNotes("c", []);
        Assert.Equal(1, index.AddOcrPages("c", [new PageEntry(1, "ocr cwords dup", true), new PageEntry(5, "ocr cwords", true)]));

        Assert.Empty(index.Search(LibraryQuery.Parse("awords")));
        Assert.Equal(["b"], index.Search(LibraryQuery.Parse("bwords")).Select(h => h.ContentId).Distinct());
        Assert.Single(index.Search(LibraryQuery.Parse("bwords"))); // only the replaced page; old outline/notes gone
        var c = index.Search(LibraryQuery.Parse("cwords"));
        Assert.Equal(4, c.Count); // two pages, the outline entry, and the OCR page 5 (page 1 already had text)
        Assert.DoesNotContain(c, h => h.Source == HitSource.Note);
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
    public void Details_TitleAndAuthorAreSearchable_AndLabelTheDocument()
    {
        using var index = Open();
        var info = new DocumentInfo("Attention Is All You Need", "Ashish Vaswani; Noam Shazeer", "Transformers", "attention, translation");
        index.ReplaceDocument(Doc("a", "1706.03762v7.pdf", "the body text") with { Info = info });
        var hit = Assert.Single(index.Search(LibraryQuery.Parse("vaswani")));
        Assert.Equal((HitSource.Details, (int?)null), (hit.Source, hit.Page));
        Assert.Equal(("Attention Is All You Need", "Ashish Vaswani; Noam Shazeer"), (hit.Title, hit.Authors));
        Assert.Single(index.Search(LibraryQuery.Parse("translation")));
        var doc = index.GetDocument("a")!;
        Assert.Equal(("Attention Is All You Need", "Ashish Vaswani; Noam Shazeer"), (doc.Title, doc.Authors));

        var group = Assert.Single(LibraryResults.Group(index.Search(LibraryQuery.Parse("body")), exists: _ => true));
        Assert.Equal("Attention Is All You Need", group.DisplayName); // a page hit still carries the title

        // A rename re-indexes the file name only; the metadata stays.
        index.RecordOpen("a", "/papers/renamed.pdf", 0, DateTime.UtcNow);
        Assert.Single(index.Search(LibraryQuery.Parse("vaswani")));

        index.Remove("a");
        Assert.Empty(index.Search(LibraryQuery.Parse("vaswani")));
    }

    [Theory]
    [InlineData("Attention Is All You Need", "Attention Is All You Need")]
    [InlineData("  Robust   Regression ", "Robust Regression")]
    [InlineData("Microsoft Word - draft3.docx", null)]
    [InlineData("Microsoft Word - Kernel Methods in Practice", "Kernel Methods in Practice")]
    [InlineData("main.tex", null)]
    [InlineData("paper.dvi", null)]
    [InlineData("Untitled", null)]
    [InlineData("C:\\Users\\me\\thesis", null)]
    [InlineData("12", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void InfoCleaner_DropsTitlesThatAreNotTitles(string? raw, string? expected)
        => Assert.Equal(expected, DocumentInfoCleaner.CleanTitle(raw));

    [Fact]
    public void InfoCleaner_DropsPlaceholderAuthors_KeepsRealOnes()
    {
        var info = DocumentInfoCleaner.From(new RailReader.Core.Services.PdfMetadata(
            "Chapter 2 Literature Review", "python-docx", null, null, null, null));
        Assert.Equal(("Chapter 2 Literature Review", (string?)null), (info.Title, info.Author));
        Assert.Equal("Jane Q. Example", DocumentInfoCleaner.From(new RailReader.Core.Services.PdfMetadata(
            null, "Jane Q. Example", null, null, null, null)).Author);
        Assert.Null(DocumentInfoCleaner.From(new RailReader.Core.Services.PdfMetadata(null, "Administrator", null, null, null, null)).Author);
    }

    [Fact]
    public void Indexer_ReadsPdfMetadata()
    {
        var path = Path.Combine(_dir, "meta.pdf");
        using (var stream = File.Create(path))
        using (var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
               {
                   Title = "Bootstrap Methods for Dependent Data", Author = "A. Statistician",
                   Subject = "Resampling", Keywords = "bootstrap; time series",
               }))
        {
            using var font = new SKFont { Size = 24 };
            using var paint = new SKPaint { Color = SKColors.Black };
            using (var canvas = doc.BeginPage(612, 792))
                canvas.DrawText("Some body text", 72, 100, SKTextAlign.Left, font, paint);
            doc.EndPage();
            doc.Close();
        }
        var content = LibraryIndexer.Extract(path, "meta", new SkiaPdfServiceFactory());
        Assert.Equal(new DocumentInfo("Bootstrap Methods for Dependent Data", "A. Statistician", "Resampling", "bootstrap; time series"),
            content.Info);
    }

    [Fact]
    public void Schema2_IsMigratedInPlace_KeepingHistory_AndMarkedForReextraction()
    {
        // A version-2 index as the previous release wrote it: one document opened before, one removed.
        using (var conn = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            conn.Open();
            foreach (var sql in new[]
            {
                "create table meta (key text primary key, value text)",
                "create table removed (content_id text primary key)",
                """
                create table documents (doc_key integer primary key, content_id text not null unique, last_path text,
                  file_name text not null, page_count integer not null default 0, file_size integer not null default 0,
                  file_mtime integer not null default 0, indexer_version integer not null default 0,
                  encrypted integer not null default 0, state integer not null default 0, indexed_utc text,
                  last_opened_utc text, last_page integer)
                """,
                "create virtual table page_fts using fts5(body, doc_key unindexed, page unindexed, from_ocr unindexed, tokenize = 'porter unicode61 remove_diacritics 2')",
                "create virtual table outline_fts using fts5(title, doc_key unindexed, page unindexed, tokenize = 'porter unicode61 remove_diacritics 2')",
                "create virtual table note_fts using fts5(marked_text, comment, author, category, doc_key unindexed, page unindexed, kind unindexed, color unindexed, native_id unindexed, source unindexed, tokenize = 'porter unicode61 remove_diacritics 2')",
                "create virtual table name_fts using fts5(name, doc_key unindexed, tokenize = 'porter unicode61 remove_diacritics 2')",
                "insert into meta values ('schema_version', '2'), ('backfill', 'off')",
                "insert into removed values ('gone')",
                "insert into documents(doc_key, content_id, last_path, file_name, page_count, indexer_version, last_opened_utc, last_page) values (1, 'a', '/papers/kept.pdf', 'kept.pdf', 3, 1, '2026-11-01T00:00:00.0000000Z', 2)",
                "insert into name_fts(rowid, name, doc_key) values (1048576, 'kept', 1)",
                "insert into page_fts(rowid, body, doc_key, page, from_ocr) values (1048576, 'still searchable', 1, 0, 0)",
            })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        using var index = LibraryIndex.OpenWritable(Db, out bool recovered);
        Assert.False(recovered);
        var doc = index.GetDocument("a")!;
        Assert.Equal((2, (string?)null), (doc.LastPage, doc.Title));
        Assert.NotNull(doc.LastOpenedUtc);
        Assert.True(index.IsRemoved("gone"));
        Assert.False(index.BackfillEnabled);
        Assert.Single(index.Search(LibraryQuery.Parse("searchable")));
        Assert.True(index.NeedsIndex("a", "/papers/kept.pdf")); // older indexer: re-extracted with its metadata

        index.ReplaceDocument(Doc("a", "kept.pdf", "new text") with { Info = new DocumentInfo("Kept Paper", null, null, null) });
        Assert.Equal("Kept Paper", index.GetDocument("a")!.Title);
        Assert.Single(index.Search(LibraryQuery.Parse("kept paper"), sources: new HashSet<HitSource> { HitSource.Details }));
    }
}
