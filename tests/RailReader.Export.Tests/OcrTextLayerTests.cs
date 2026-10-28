using RailReader.Core.Ocr.RapidOcr;
using RailReader.Renderer.Skia;
using RailReader2.Services;
using RailReader2.ViewModels;
using SkiaSharp;
using Xunit;

namespace RailReader.Export.Tests;

/// <summary>The shared text-layer job (railreader2#273) behind the CLI's <c>ocr</c> command and the app's action.</summary>
public sealed class OcrTextLayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rr2_ocr_test_{Guid.NewGuid():N}");
    private readonly SkiaPdfServiceFactory _factory = new();

    public OcrTextLayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Page 0 has real text; page 1 only a picture of text, as a scan does.</summary>
    private string CreateMixedPdf()
    {
        var path = Path.Combine(_dir, "mixed.pdf");
        using var font = new SKFont { Size = 40 };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using (var doc = SKDocument.CreatePdf(path))
        {
            using (var canvas = doc.BeginPage(612, 792))
                canvas.DrawText("Native text page", 72, 100, SKTextAlign.Left, font, paint);
            doc.EndPage();

            // Rendered at 2x, so the picture is crisp once drawn at page size.
            using var bitmap = new SKBitmap(1224, 1584);
            using (var bc = new SKCanvas(bitmap))
            {
                bc.Clear(SKColors.White);
                using var big = new SKFont { Size = 80 };
                bc.DrawText("HELLO SCANNED WORLD", 144, 300, SKTextAlign.Left, big, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using (var canvas = doc.BeginPage(612, 792))
                canvas.DrawImage(image, SKRect.Create(612, 792));
            doc.EndPage();
            doc.Close();
        }
        return path;
    }

    [Fact]
    public void PagesNeedingOcr_SkipsPagesWithNativeText()
    {
        var path = CreateMixedPdf();
        var pdf = _factory.CreatePdfService(path);
        using (pdf as IDisposable)
            Assert.Equal([1], OcrTextLayer.PagesNeedingOcr(pdf, _factory.CreatePdfTextService()));
    }

    [Fact]
    public void Run_WritesAnOcrLayerThatReadsBackAsOcrText()
    {
        Assert.NotNull(OcrModelLocator.LocateDefault()); // the bundled models must be beside the test binaries
        var path = CreateMixedPdf();
        var output = OcrTextLayer.DefaultOutputPath(path);
        var pdf = _factory.CreatePdfService(path);
        var text = _factory.CreatePdfTextService();
        using (pdf as IDisposable)
        using (var ocr = new RapidOcrService())
        {
            var result = OcrTextLayer.Run(pdf, path, ocr, OcrTextLayer.PagesNeedingOcr(pdf, text), output);
            Assert.Equal(1, result.PagesWritten);
        }

        var copy = _factory.CreatePdfService(output);
        using (copy as IDisposable)
        {
            var page = text.ExtractPageText(copy.PdfBytes, 1, copy.Password);
            Assert.True(page.FromOcr);
            Assert.Contains("HELLO", page.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(text.ExtractPageText(copy.PdfBytes, 0, copy.Password).FromOcr);
            // A re-run on the copy recognises that page again, so the writer replaces its own layer.
            Assert.Equal([1], OcrTextLayer.PagesNeedingOcr(copy, text));
        }
    }

    [Fact]
    public void Run_RefusesToWriteOverTheSource()
    {
        var path = CreateMixedPdf();
        var pdf = _factory.CreatePdfService(path);
        using (pdf as IDisposable)
        using (var ocr = new RapidOcrService())
            Assert.Throws<InvalidOperationException>(() => OcrTextLayer.Run(pdf, path, ocr, [1], path));
    }

    [Fact]
    public void OutputPaths_UseTheOcrSuffixAndNeverOverwrite()
    {
        var source = Path.Combine(_dir, "scan.pdf");
        Assert.Equal(Path.Combine(_dir, "scan_ocr.pdf"), OcrTextLayer.DefaultOutputPath(source));
        Assert.Equal(Path.Combine(_dir, "scan_ocr.pdf"), MainWindowViewModel.FreeTextLayerPath(source));

        File.WriteAllText(Path.Combine(_dir, "scan_ocr.pdf"), "");
        Assert.Equal(Path.Combine(_dir, "scan_ocr (2).pdf"), MainWindowViewModel.FreeTextLayerPath(source));
    }
}
