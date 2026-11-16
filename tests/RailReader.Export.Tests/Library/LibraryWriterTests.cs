using Microsoft.Data.Sqlite;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.Library;
using SkiaSharp;
using Xunit;

namespace RailReader.Export.Tests.Library;

public sealed class LibraryWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_libwriter_").FullName;
    private string Db => Path.Combine(_dir, "library.db");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakePdf(string name, params string[] pages)
    {
        var path = Path.Combine(_dir, name);
        using var doc = SKDocument.CreatePdf(path);
        using var font = new SKFont { Size = 24 };
        using var paint = new SKPaint { Color = SKColors.Black };
        foreach (var line in pages)
        {
            using (var canvas = doc.BeginPage(612, 792))
                if (line.Length > 0) canvas.DrawText(line, 72, 100, SKTextAlign.Left, font, paint);
            doc.EndPage();
        }
        doc.Close();
        return path;
    }

    private IReadOnlyList<LibraryHit> Search(string q)
    {
        using var index = LibraryIndex.OpenReadOnly(Db)!;
        return index.Search(LibraryQuery.Parse(q));
    }

    private static AnnotationFile Notes(string text)
    {
        var f = new AnnotationFile();
        f.Pages[0] = [new TextNoteAnnotation { Text = text, Contents = text, Color = "#FFFF00" }];
        return f;
    }

    [Fact]
    public void Opened_IndexesTheDocument_ThenRefreshesNotesWithoutReextracting()
    {
        var pdf = MakePdf("paper.pdf", "kernel ridge regression");
        using (var w = new LibraryWriter(Db, new SkiaPdfServiceFactory()))
        {
            w.Categories = new Dictionary<string, string> { ["#FFFF00"] = "Definition" };
            w.DocumentOpened(pdf, "id1", encrypted: false, Notes("first note"), page: 0);
            Assert.True(w.WaitForForeground(Wait));
            Assert.Single(Search("kernel"));
            Assert.Single(Search("first"));
            Assert.Single(Search("definition"));

            w.DocumentOpened(pdf, "id1", encrypted: false, Notes("second note"), page: 0);
            Assert.True(w.WaitForForeground(Wait));
            Assert.Empty(Search("first"));
            Assert.Single(Search("second"));
        }
        using var index = LibraryIndex.OpenReadOnly(Db)!;
        Assert.Equal(0, index.GetDocument("id1")!.LastPage);
    }

    [Fact]
    public void Closed_AddsOcrOnlyText_AndRecordsThePage()
    {
        var pdf = MakePdf("scan.pdf", "native words", "");
        using (var w = new LibraryWriter(Db, new SkiaPdfServiceFactory()))
        {
            w.DocumentOpened(pdf, "scan", false, null, 0);
            w.DocumentClosed(pdf, "scan", false, null, [new PageEntry(1, "recognised words", true)], page: 1);
            Assert.True(w.WaitForForeground(Wait));
        }
        var hit = Assert.Single(Search("recognised"));
        Assert.True(hit.FromOcr);
        using var index = LibraryIndex.OpenReadOnly(Db)!;
        Assert.Equal(1, index.GetDocument("scan")!.LastPage);
    }

    [Fact]
    public void Encrypted_IndexesNoText_AndIsLocked()
    {
        var pdf = MakePdf("secret.pdf", "confidential words");
        using (var w = new LibraryWriter(Db, new SkiaPdfServiceFactory()))
        {
            w.DocumentOpened(pdf, "secret", encrypted: true, Notes("private note"), 0);
            w.DocumentClosed(pdf, "secret", encrypted: true, Notes("private note"), [new PageEntry(0, "ocr words", true)], 0);
            Assert.True(w.WaitForForeground(Wait));
        }
        Assert.Empty(Search("confidential"));
        Assert.Empty(Search("private"));
        Assert.Empty(Search("ocr"));
        Assert.Single(Search("secret")); // file name only
        using var index = LibraryIndex.OpenReadOnly(Db)!;
        Assert.Equal(DocumentState.Locked, index.GetDocument("secret")!.State);
    }

    [Fact]
    public void Dispose_FinishesQueuedJobs()
    {
        var pdf = MakePdf("a.pdf", "queued words");
        var w = new LibraryWriter(Db, new SkiaPdfServiceFactory());
        w.DocumentOpened(pdf, "a", false, null, 0);
        w.Dispose(Wait);
        Assert.Single(Search("queued"));
        w.DocumentOpened(pdf, "a", false, null, 0); // after dispose: ignored, no throw
    }

    [Fact]
    public void UnopenableDatabase_TurnsTheWriterOff()
    {
        File.WriteAllText(Path.Combine(_dir, "file"), "");
        var errors = new List<string>();
        using var w = new LibraryWriter(Path.Combine(_dir, "file", "library.db"), new SkiaPdfServiceFactory())
        {
            OnError = (m, _) => { lock (errors) errors.Add(m); },
        };
        w.DocumentOpened(MakePdf("b.pdf", "x"), "b", false, null, 0);
        Assert.True(w.WaitForForeground(Wait));
        lock (errors) Assert.Contains(errors, m => m.Contains("library search is off"));
        w.DocumentOpened(MakePdf("c.pdf", "y"), "c", false, null, 0); // disabled: ignored
        Assert.True(w.WaitForForeground(Wait));
    }

    [Fact]
    public void Backfill_WaitsWhileBusy()
    {
        using var w = new LibraryWriter(Db, new SkiaPdfServiceFactory()) { BusyRecheck = TimeSpan.FromMilliseconds(50) };
        w.IsBusy = true;
        w.Backfill([Path.Combine(_dir, "missing.pdf")]);
        Thread.Sleep(300);
        Assert.Equal(1, w.BackfillRemaining);
        w.IsBusy = false;
        var deadline = DateTime.UtcNow + Wait;
        while (w.BackfillRemaining > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.Equal(0, w.BackfillRemaining);
    }
}
