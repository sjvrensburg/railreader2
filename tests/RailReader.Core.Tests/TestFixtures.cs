using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader.Core.Tests;

/// <summary>
/// Creates minimal test PDFs using SkiaSharp's PDF backend.
/// </summary>
public static class TestFixtures
{
    private static readonly List<string> s_tempFiles = [];

    static TestFixtures()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var file in s_tempFiles)
                try { File.Delete(file); } catch { }
        };
    }

    /// <summary>
    /// Returns path to a new 3-page test PDF. Each call creates a unique file
    /// to avoid file locking conflicts between concurrent tests.
    /// </summary>
    public static string GetTestPdfPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_test_{Guid.NewGuid():N}.pdf");
        CreateTestPdf(path, pageCount: 3);
        s_tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Creates the standard SkiaPdfServiceFactory for tests.
    /// </summary>
    public static IPdfServiceFactory CreatePdfFactory() => new SkiaPdfServiceFactory();

    /// <summary>
    /// Configures a DocumentState for rail mode testing: injects synthetic analysis,
    /// sets zoom above threshold, and activates rail navigation.
    /// </summary>
    public static void SetupRailMode(DocumentState doc, AppConfig config,
        double vpWidth = 800, double vpHeight = 600)
    {
        var analysis = new PageAnalysis();
        var block = new LayoutBlock
        {
            ClassId = 22, BBox = new BBox(72, 72, 468, 200),
            Confidence = 0.9f, Order = 0,
        };
        for (int i = 0; i < 5; i++)
            block.Lines.Add(new LineInfo(72 + i * 20, 16));
        analysis.Blocks.Add(block);
        doc.AnalysisCache[doc.CurrentPage] = analysis;
        doc.Rail.SetAnalysis(analysis, config.NavigableClasses);
        doc.Camera.Zoom = config.RailZoomThreshold + 1;
        doc.Rail.UpdateZoom(doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, vpWidth, vpHeight);
    }

    public static void CreateTestPdf(string path, int pageCount = 3)
    {
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 14);

        for (int i = 0; i < pageCount; i++)
        {
            using var canvas = doc.BeginPage(612, 792); // US Letter
            canvas.DrawText($"Page {i + 1} of {pageCount}", 72, 72, font, paint);
            canvas.DrawText("This is a test paragraph with some text content.", 72, 120, font, paint);
            canvas.DrawText("Second line of text for testing purposes.", 72, 140, font, paint);
            doc.EndPage();
        }

        doc.Close();
    }
}
