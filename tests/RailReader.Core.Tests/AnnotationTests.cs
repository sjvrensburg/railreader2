using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AnnotationTests : IDisposable
{
    private readonly DocumentState _state;
    private readonly AnnotationFileManager _manager;

    public AnnotationTests()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _state = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, marshaller);
        _state.LoadPageBitmap();
        _manager = new AnnotationFileManager(marshaller);
        _state.LoadAnnotations(_manager);
    }

    public void Dispose()
    {
        _state.Dispose();
        _manager.Dispose();
    }

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

        bool hit = AnnotationGeometry.HitTest(highlight, 50, 20);
        Assert.True(hit);

        bool miss = AnnotationGeometry.HitTest(highlight, 500, 500);
        Assert.False(miss);
    }

    [Fact]
    public void SharedAnnotationFile_TwoTabsSameFile()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();

        using var manager = new AnnotationFileManager(marshaller);

        var stateA = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, marshaller);
        stateA.LoadPageBitmap();
        stateA.LoadAnnotations(manager);

        var stateB = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, marshaller);
        stateB.LoadPageBitmap();
        stateB.LoadAnnotations(manager);

        // Both tabs share the same AnnotationFile instance
        Assert.Same(stateA.Annotations, stateB.Annotations);

        // Annotation added via tab A is visible in tab B
        stateA.AddAnnotation(0, new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 10, 100, 20)],
        });
        Assert.Single(stateB.Annotations.Pages[0]);

        // Annotation added via tab B is visible in tab A
        stateB.AddAnnotation(1, new HighlightAnnotation
        {
            Rects = [new HighlightRect(50, 50, 80, 15)],
        });
        Assert.Single(stateA.Annotations.Pages[1]);

        // Undo in tab A only affects tab A's action (page 0), not tab B's (page 1)
        stateA.Undo();
        Assert.Empty(stateA.Annotations.Pages[0]);
        Assert.Single(stateB.Annotations.Pages[1]); // tab B's annotation intact

        stateA.Dispose();
        stateB.Dispose();
    }

    [Fact]
    public void MergeInto_AppendsAnnotationsAndBookmarks()
    {
        var target = new AnnotationFile();
        target.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(0, 0, 10, 10)] }];
        target.Bookmarks.Add(new BookmarkEntry { Name = "Intro", Page = 0 });

        var imported = new AnnotationFile();
        imported.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(20, 20, 30, 30)] }];
        imported.Pages[1] = [new FreehandAnnotation { Points = [new PointF(5, 5)] }];
        imported.Bookmarks.Add(new BookmarkEntry { Name = "Intro", Page = 0 }); // duplicate
        imported.Bookmarks.Add(new BookmarkEntry { Name = "Ch 2", Page = 1 }); // new

        int added = AnnotationService.MergeInto(target, imported);

        Assert.Equal(2, target.Pages[0].Count); // original + imported
        Assert.Single(target.Pages[1]);          // new page
        Assert.Equal(2, target.Bookmarks.Count); // "Intro" not duplicated, "Ch 2" added
        Assert.Equal(3, added);                  // 2 annotations + 1 bookmark
    }
}
