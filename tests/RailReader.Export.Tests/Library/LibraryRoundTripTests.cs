using Microsoft.Data.Sqlite;
using RailReader.Renderer.Skia;
using RailReader2.Library;
using SkiaSharp;
using Xunit;

namespace RailReader.Export.Tests.Library;

/// <summary>The headless self-check behind <c>RailReader2 --library-probe</c> (railreader2#298).</summary>
public sealed class LibraryRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_roundtrip_").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakePdf(string text)
    {
        var path = Path.Combine(_dir, "probe.pdf");
        using var doc = SKDocument.CreatePdf(path);
        using var font = new SKFont { Size = 24 };
        using var paint = new SKPaint { Color = SKColors.Black };
        using (var canvas = doc.BeginPage(612, 792))
            canvas.DrawText(text, 72, 100, SKTextAlign.Left, font, paint);
        doc.EndPage();
        doc.Close();
        return path;
    }

    [Fact]
    public void Passes_WhenTheTermIsFound_AndReportsEachStep()
    {
        var (ok, report) = LibraryRoundTrip.Run(MakePdf("Library search railreaderprobe check"), "railreaderprobe",
            new SkiaPdfServiceFactory(), Path.Combine(_dir, "library.db"));
        Assert.True(ok, report);
        Assert.Contains("FTS5: yes", report);
        Assert.Matches(@"Native SQLite loaded from: \S*(lib)?e_sqlite3\.(so|dll|dylib)", report); // the native library, not its managed wrapper
        Assert.Contains("1 hit(s)", report);
        Assert.EndsWith("OK" + Environment.NewLine, report);
    }

    [Fact]
    public void Fails_WhenTheTermIsMissing()
    {
        var (ok, report) = LibraryRoundTrip.Run(MakePdf("something else entirely"), "railreaderprobe",
            new SkiaPdfServiceFactory(), Path.Combine(_dir, "library.db"));
        Assert.False(ok);
        Assert.Contains("0 hit(s)", report);
    }

    [Fact]
    public void Fails_OnAnUnreadablePdf_WithoutThrowing()
    {
        var bogus = Path.Combine(_dir, "bogus.pdf");
        File.WriteAllText(bogus, "not a pdf");
        var (ok, report) = LibraryRoundTrip.Run(bogus, "x", new SkiaPdfServiceFactory(), Path.Combine(_dir, "library.db"));
        Assert.False(ok);
        Assert.Contains("state Failed", report);
    }
}
