using System.IO.Compression;
using System.Text;
using RailReader.Core.Models;
using RailReader2.Services;
using Xunit;

namespace RailReader.Export.Tests;

/// <summary>The <c>.railreading</c> format: written from explicit state (never the real config directory)
/// and read back, including what a reader must refuse.</summary>
public sealed class ReadingPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rr-package-{Guid.NewGuid():N}");
    private readonly string _pdf;
    private readonly string _package;

    public ReadingPackageTests()
    {
        Directory.CreateDirectory(_dir);
        _pdf = Path.Combine(_dir, "Smith2019.pdf");
        _package = Path.Combine(_dir, "Smith2019.railreading");
        using var doc = SkiaSharp.SKDocument.CreatePdf(_pdf);
        for (int i = 0; i < 3; i++)
        {
            using var canvas = doc.BeginPage(612, 792);
            using var font = new SkiaSharp.SKFont { Size = 24 };
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
            canvas.DrawText($"Page {i + 1}", 72, 72, SkiaSharp.SKTextAlign.Left, font, paint);
            doc.EndPage();
        }
        doc.Close();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Scratch() => Path.Combine(_dir, $"scratch-{Guid.NewGuid():N}");

    private static AnnotationFile SampleAnnotations() => new()
    {
        Pages =
        {
            [0] = [new HighlightAnnotation { Color = "#FFFF00", Rects = [new HighlightRect(10, 20, 100, 12)] }],
            [2] = [new TextNoteAnnotation { X = 50, Y = 60, Contents = "Check this proof" }],
        },
        Bookmarks = [new BookmarkEntry { Name = "Main theorem", Page = 2 }],
    };

    private static PortalSet SamplePortals() => new()
    {
        Portals =
        [
            new Portal
            {
                Id = "p1", Label = "Figure 2",
                Source = new PortalAnchor { Page = 0, Block = 3, Role = "Text", Line = 1, Ly = 0.4f },
                Target = new PortalAnchor { Page = 1, Block = 0, Role = "Figure", Nx = 0.1f, Ny = 0.2f, Nw = 0.5f, Nh = 0.3f },
                CreatedUtc = "2026-10-29T00:00:00.0000000Z",
            },
        ],
    };

    private PackageManifest WriteSample(string? contentId = "abc123")
        => ReadingPackage.Write(_pdf, _package, encrypted: false, contentId, SampleAnnotations(), SamplePortals(),
            new PackageViewState
            {
                Rotation = 1, Page = 2, Zoom = 2.5, OffsetX = -40, OffsetY = 12, HasSavedCamera = true,
                ColourEffect = ColourEffect.HighContrast,
            });

    /// <summary>Rewrites one entry of the package (or adds it) with the given bytes.</summary>
    private void ReplaceEntry(string name, byte[] bytes)
    {
        using var zip = ZipFile.Open(_package, ZipArchiveMode.Update);
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private string EntryText(string name)
    {
        using var zip = ZipFile.OpenRead(_package);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void RoundTrip_RestoresEverything()
    {
        var written = WriteSample();
        var opened = ReadingPackage.Read(_package, Scratch());

        Assert.Equal(ReadingPackage.FormatVersion, opened.Manifest.FormatVersion);
        Assert.Equal("Smith2019.pdf", opened.Manifest.Pdf.FileName);
        Assert.Equal("abc123", opened.Manifest.Pdf.ContentId);
        Assert.Equal(written.Pdf.Sha256, opened.Manifest.Pdf.Sha256);
        Assert.Equal(File.ReadAllBytes(_pdf), File.ReadAllBytes(opened.PdfPath));

        Assert.IsType<HighlightAnnotation>(Assert.Single(opened.Annotations.Pages[0]));
        Assert.Equal("Check this proof", Assert.Single(opened.Annotations.Pages[2]).Contents);
        Assert.Equal("Main theorem", Assert.Single(opened.Annotations.Bookmarks).Name);

        var portal = Assert.Single(opened.Portals.Portals);
        Assert.Equal(("p1", "Figure 2", 1, "Figure"), (portal.Id, portal.Label, portal.Source.Line, portal.Target.Role));

        Assert.Equal((1, 2, 2.5, -40.0, 12.0, true),
            (opened.View.Rotation, opened.View.Page, opened.View.Zoom, opened.View.OffsetX, opened.View.OffsetY, opened.View.HasSavedCamera));
        Assert.Equal(ColourEffect.HighContrast, opened.View.ColourEffect);
    }

    [Fact]
    public void ColourEffect_IsStoredUnderConfigJsonsName()
    {
        WriteSample();
        Assert.Contains("\"colour_effect\": \"highContrast\"", EntryText(ReadingPackage.ViewEntry));
    }

    [Fact]
    public void NoColourEffect_RoundTripsAsNull()
    {
        ReadingPackage.Write(_pdf, _package, false, null, new AnnotationFile(), new PortalSet(), new PackageViewState());
        Assert.Null(ReadingPackage.Read(_package, Scratch()).View.ColourEffect);
    }

    [Fact]
    public void UnknownContentId_IsThePdfHash()
    {
        var manifest = WriteSample(contentId: null);
        Assert.Equal(manifest.Pdf.Sha256, manifest.Pdf.ContentId);
    }

    [Fact]
    public void Write_RefusesToReplaceThePdf()
        => Assert.Throws<InvalidOperationException>(() =>
            ReadingPackage.Write(_pdf, _pdf, null, false, new PackageViewState(), _ => null));

    [Fact]
    public void TamperedPdf_IsRejected()
    {
        WriteSample();
        var bytes = File.ReadAllBytes(_pdf);
        bytes[^10] ^= 0xFF;
        ReplaceEntry(ReadingPackage.PdfEntry, bytes);
        var ex = Assert.Throws<InvalidDataException>(() => ReadingPackage.Read(_package, Scratch()));
        Assert.Contains("doesn't match", ex.Message);
    }

    [Fact]
    public void NewerFormat_IsRejected()
    {
        WriteSample();
        var manifest = EntryText(ReadingPackage.ManifestEntry).Replace("\"format_version\": 1", "\"format_version\": 2");
        ReplaceEntry(ReadingPackage.ManifestEntry, Encoding.UTF8.GetBytes(manifest));
        var ex = Assert.Throws<InvalidDataException>(() => ReadingPackage.Read(_package, Scratch()));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void NotAZip_IsRejected()
    {
        File.WriteAllText(_package, "not a zip");
        Assert.Throws<InvalidDataException>(() => ReadingPackage.Read(_package, Scratch()));
    }

    [Fact]
    public void MissingManifest_IsRejected()
    {
        WriteSample();
        using (var zip = ZipFile.Open(_package, ZipArchiveMode.Update))
            zip.GetEntry(ReadingPackage.ManifestEntry)!.Delete();
        Assert.Throws<InvalidDataException>(() => ReadingPackage.Read(_package, Scratch()));
    }

    [Fact]
    public void UnknownAndTraversingEntries_AreIgnored()
    {
        WriteSample();
        ReplaceEntry("../evil.txt", [1, 2, 3]);
        ReplaceEntry("extra/notes.json", [4]);
        string scratch = Scratch();

        ReadingPackage.Read(_package, scratch);
        var (_, pdfOut) = ReadingPackage.Unpack(_package, Path.Combine(_dir, "out"));

        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(scratch, "..", "evil.txt")));
        Assert.Equal(["annotations.json", "document.pdf"],   // portals and view are read in memory
            Directory.GetFiles(scratch).Select(Path.GetFileName).Order());
        Assert.Equal(Path.Combine(_dir, "out", "Smith2019.pdf"), pdfOut);
        Assert.Equal(["Smith2019.pdf", "annotations.json", "manifest.json", "portals.json", "view.json"],
            Directory.GetFiles(Path.Combine(_dir, "out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Unpack_KeepsOnlyTheFileNamesLastSegment()
    {
        WriteSample();
        var manifest = EntryText(ReadingPackage.ManifestEntry).Replace("\"Smith2019.pdf\"", "\"../../escaped.pdf\"");
        ReplaceEntry(ReadingPackage.ManifestEntry, Encoding.UTF8.GetBytes(manifest));

        var (_, pdfOut) = ReadingPackage.Unpack(_package, Path.Combine(_dir, "out"));

        Assert.Equal(Path.Combine(_dir, "out", "escaped.pdf"), pdfOut);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.pdf")));
    }

    [Fact]
    public void Unpack_AFileNameThatIsAnEntryName_FallsBackToDocumentPdf()
    {
        WriteSample();
        var manifest = EntryText(ReadingPackage.ManifestEntry).Replace("\"Smith2019.pdf\"", "\"manifest.json\"");
        ReplaceEntry(ReadingPackage.ManifestEntry, Encoding.UTF8.GetBytes(manifest));

        var (_, pdfOut) = ReadingPackage.Unpack(_package, Path.Combine(_dir, "out"));

        Assert.Equal(Path.Combine(_dir, "out", ReadingPackage.PdfEntry), pdfOut);
        Assert.Contains("\"format\"", File.ReadAllText(Path.Combine(_dir, "out", ReadingPackage.ManifestEntry)));
    }

    [Fact]
    public void ManifestWithoutPdf_IsRejectedAsDamaged()
    {
        WriteSample();
        ReplaceEntry(ReadingPackage.ManifestEntry,
            Encoding.UTF8.GetBytes("""{ "format": "railreading", "format_version": 1, "pdf": null }"""));
        var ex = Assert.Throws<InvalidDataException>(() => ReadingPackage.Read(_package, Scratch()));
        Assert.Contains("damaged", ex.Message);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("sepia")]
    public void UnknownColourEffect_ReadsAsNone(string stored)
        => Assert.Null(new PackageViewState { ColourEffectName = stored }.ColourEffect);

    [Fact]
    public void Unpack_RefusesToOverwrite()
    {
        WriteSample();
        string outDir = Path.Combine(_dir, "out");
        ReadingPackage.Unpack(_package, outDir);
        Assert.Throws<IOException>(() => ReadingPackage.Unpack(_package, outDir));
    }

    [Fact]
    public void SidecarPart_DropsAnnotationsStoredInThePdf()
    {
        var loaded = SampleAnnotations();
        loaded.Pages[0].Add(new HighlightAnnotation { Source = AnnotationSource.InPdf, NativeId = "nm-1" });
        loaded.Pages[1] = [new HighlightAnnotation { Source = AnnotationSource.InPdf, NativeId = "nm-2" }];

        var part = ReadingPackage.SidecarPart(loaded);

        Assert.Single(part.Pages[0]);
        Assert.False(part.Pages.ContainsKey(1));
        Assert.Single(part.Pages[2]);
        Assert.Single(part.Bookmarks);
        Assert.Equal(2, loaded.Pages[0].Count);   // the input is not modified
    }

    [Fact]
    public void SidecarPart_OfNothing_IsEmpty()
    {
        var part = ReadingPackage.SidecarPart(null);
        Assert.Empty(part.Pages);
        Assert.Empty(part.Bookmarks);
    }

    [Fact]
    public void OversizedEntry_IsRejected()
    {
        using var src = new MemoryStream(new byte[100]);
        Assert.Throws<InvalidDataException>(() => ReadingPackage.CopyCapped(src, Stream.Null, max: 99, hash: false));
    }

    [Theory]
    [InlineData("a.railreading", true)]
    [InlineData("A.RAILREADING", true)]
    [InlineData("a.pdf", false)]
    public void IsPackagePath_MatchesTheExtension(string path, bool expected)
        => Assert.Equal(expected, ReadingPackage.IsPackagePath(path));
}
