using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AnnotationTests : IDisposable
{
    private readonly DocumentState _state;

    public AnnotationTests()
    {
        var config = new AppConfig();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _state = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, new SynchronousThreadMarshaller());
        _state.LoadPageBitmap();
        _state.LoadAnnotations();
    }

    public void Dispose() => _state.Dispose();

    [Fact]
    public void AddAnnotation_AddsToPage()
    {
        var highlight = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 10, 100, 20)],
            Color = "#FFFF00",
            Opacity = 0.4f,
        };

        _state.AddAnnotation(0, highlight);

        Assert.NotNull(_state.Annotations);
        Assert.True(_state.Annotations.Pages.ContainsKey(0));
        Assert.Single(_state.Annotations.Pages[0]);
    }

    [Fact]
    public void Undo_RemovesAnnotation()
    {
        var highlight = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 10, 100, 20)],
            Color = "#FFFF00",
            Opacity = 0.4f,
        };

        _state.AddAnnotation(0, highlight);
        Assert.Single(_state.Annotations!.Pages[0]);

        _state.Undo();
        Assert.Empty(_state.Annotations.Pages[0]);
    }

    [Fact]
    public void Redo_RestoresAnnotation()
    {
        var highlight = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 10, 100, 20)],
            Color = "#FFFF00",
            Opacity = 0.4f,
        };

        _state.AddAnnotation(0, highlight);
        _state.Undo();
        _state.Redo();

        Assert.Single(_state.Annotations!.Pages[0]);
    }

    [Fact]
    public void RemoveAnnotation_RemovesFromPage()
    {
        var highlight = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 10, 100, 20)],
            Color = "#FFFF00",
            Opacity = 0.4f,
        };

        _state.AddAnnotation(0, highlight);
        _state.RemoveAnnotation(0, highlight);

        Assert.Empty(_state.Annotations!.Pages[0]);
    }

    [Fact]
    public void HitTest_FindsAnnotation()
    {
        var highlight = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 10, 100, 20)],
            Color = "#FFFF00",
            Opacity = 0.4f,
        };

        bool hit = AnnotationRenderer.HitTest(highlight, 50, 20);
        Assert.True(hit);

        bool miss = AnnotationRenderer.HitTest(highlight, 500, 500);
        Assert.False(miss);
    }
}
