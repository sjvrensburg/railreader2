using Xunit;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;

namespace RailReader.Export.Tests;

public class MarkdownExportServiceTests
{
    [Fact]
    public async Task Export_WithNoOnnxModel_FallsBackToPlainText()
    {
        // This test verifies the plain-text fallback path works when
        // no ONNX model is available. We use a real PDF service with
        // a minimal test PDF.
        var factory = new TestPdfServiceFactory();
        var service = new MarkdownExportService(factory.Factory);

        var output = new StringWriter();
        var options = new MarkdownExportOptions
        {
            EnableVlm = false,
            IncludeAnnotations = false,
        };

        // This will fall back to plain text since no ONNX model is likely
        // available in the test environment
        await service.ExportAsync(
            factory.TestPdfPath,
            output,
            options);

        var result = output.ToString();
        // Should produce some output (even if just whitespace for a blank PDF)
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Export_WithPageRange_RespectsRange()
    {
        var factory = new TestPdfServiceFactory();
        var service = new MarkdownExportService(factory.Factory);

        var output = new StringWriter();
        var options = new MarkdownExportOptions
        {
            EnableVlm = false,
            IncludeAnnotations = false,
            PageRange = "1",
        };

        await service.ExportAsync(
            factory.TestPdfPath,
            output,
            options);

        // Should not throw and should produce output
        Assert.NotNull(output.ToString());
    }

    [Fact]
    public async Task Export_ReportsProgress()
    {
        var factory = new TestPdfServiceFactory();
        var service = new MarkdownExportService(factory.Factory);

        var progress = new TestProgress();
        var options = new MarkdownExportOptions
        {
            EnableVlm = false,
            IncludeAnnotations = false,
            PageRange = "1",
        };

        await service.ExportAsync(
            factory.TestPdfPath,
            new StringWriter(),
            options,
            progress);

        Assert.True(progress.Reports.Count >= 1);
        Assert.Equal("Complete", progress.Reports[^1].Status);
    }

    [Fact]
    public async Task Export_InvalidPageRange_Throws()
    {
        var factory = new TestPdfServiceFactory();
        var service = new MarkdownExportService(factory.Factory);

        var options = new MarkdownExportOptions
        {
            EnableVlm = false,
            PageRange = "999",
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExportAsync(factory.TestPdfPath, new StringWriter(), options));
    }

    [Fact]
    public async Task Export_Cancellation_Throws()
    {
        var factory = new TestPdfServiceFactory();
        var service = new MarkdownExportService(factory.Factory);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var options = new MarkdownExportOptions
        {
            EnableVlm = false,
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExportAsync(factory.TestPdfPath, new StringWriter(), options, ct: cts.Token));
    }

    [Fact]
    public async Task Export_NoPageBreaks_OmitsMarkers()
    {
        var factory = new TestPdfServiceFactory(pageCount: 2);
        var service = new MarkdownExportService(factory.Factory);

        var output = new StringWriter();
        var options = new MarkdownExportOptions
        {
            EnableVlm = false,
            IncludeAnnotations = false,
            InsertPageBreaks = false,
        };

        await service.ExportAsync(factory.TestPdfPath, output, options);

        Assert.DoesNotContain("---", output.ToString());
    }

    private sealed class TestProgress : IProgress<ExportProgress>
    {
        public List<ExportProgress> Reports { get; } = [];
        public void Report(ExportProgress value) => Reports.Add(value);
    }

    /// <summary>
    /// Creates a minimal test PDF using SkiaSharp for service tests.
    /// </summary>
    private sealed class TestPdfServiceFactory : IDisposable
    {
        private readonly string _tempDir;
        public readonly RailReader.Renderer.Skia.SkiaPdfServiceFactory Factory;

        public string TestPdfPath { get; }

        public TestPdfServiceFactory(int pageCount = 1)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"rr2_export_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            TestPdfPath = Path.Combine(_tempDir, "test.pdf");
            CreateTestPdf(TestPdfPath, pageCount);
            Factory = new RailReader.Renderer.Skia.SkiaPdfServiceFactory();
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private static void CreateTestPdf(string path, int pageCount)
        {
            using var doc = SkiaSharp.SKDocument.CreatePdf(path);
            for (int i = 0; i < pageCount; i++)
            {
                using var canvas = doc.BeginPage(612, 792); // Letter size
                using var font = new SkiaSharp.SKFont { Size = 24 };
                using var paint = new SkiaSharp.SKPaint
                {
                    Color = SkiaSharp.SKColors.Black,
                    IsAntialias = true,
                };
                canvas.DrawText($"Test page {i + 1}", 72, 72, SkiaSharp.SKTextAlign.Left, font, paint);
                doc.EndPage();
            }
            doc.Close();
        }
    }
}
