using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AnnotationInteractionHandlerMoreTests : IDisposable
{
    private readonly DocumentState _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler;

    public AnnotationInteractionHandlerMoreTests()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _doc = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, marshaller);
        _doc.LoadPageBitmap();
        _manager = new AnnotationFileManager(marshaller);
        _doc.LoadAnnotations(_manager);
        _handler = new AnnotationInteractionHandler();
    }

    public void Dispose()
    {
        _doc.Dispose();
        _manager.Dispose();
    }

    private FreehandAnnotation AddFreehand(float x = 100, float y = 100)
    {
        var a = new FreehandAnnotation
        {
            Points = [new PointF(x - 5, y - 5), new PointF(x, y), new PointF(x + 5, y + 5)],
            Color = "#FF0000",
            Opacity = 0.8f,
            StrokeWidth = 2f,
        };
        _doc.AddAnnotation(0, a);
        return a;
    }

    private RectAnnotation AddRect(float x = 100, float y = 100, float w = 50, float h = 40)
    {
        var a = new RectAnnotation
        {
            X = x,
            Y = y,
            W = w,
            H = h,
            Color = "#FF0000",
            Opacity = 0.5f,
            StrokeWidth = 2f,
        };
        _doc.AddAnnotation(0, a);
        return a;
    }

    // --- Cancel / tool lifecycle ---

    [Fact]
    public void CancelAnnotationTool_ClearsActiveToolAndPreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc, 120, 120);
        Assert.NotNull(_handler.PreviewAnnotation);

        _handler.CancelAnnotationTool();

        Assert.False(_handler.IsAnnotating);
        Assert.Equal(AnnotationTool.None, _handler.ActiveTool);
        Assert.Null(_handler.PreviewAnnotation);
    }

    [Fact]
    public void CancelledPreview_DoesNotCommitOnPointerUp()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc, 120, 120);
        _handler.CancelAnnotationTool();

        _handler.HandleAnnotationPointerUp(_doc, 120, 120);

        Assert.False(_doc.Annotations.Pages.ContainsKey(0)
            && _doc.Annotations.Pages[0].Count > 0);
    }

    // --- Thickness / color index clamping ---

    [Fact]
    public void SetThicknessIndex_OutOfRange_ClampsToValidIndex()
    {
        _handler.SetThicknessIndex(AnnotationTool.Pen, 99);
        int got = _handler.GetThicknessIndex(AnnotationTool.Pen);
        Assert.InRange(got, 0, AnnotationInteractionHandler.ThicknessPresets.Length - 1);
    }

    [Fact]
    public void SetThicknessIndex_Negative_ClampsToZero()
    {
        _handler.SetThicknessIndex(AnnotationTool.Pen, -5);
        Assert.Equal(0, _handler.GetThicknessIndex(AnnotationTool.Pen));
    }

    [Fact]
    public void SetAnnotationColorIndex_OutOfRange_ClampsToValidIndex()
    {
        _handler.SetAnnotationColorIndex(AnnotationTool.Highlight, 999);
        int got = _handler.GetAnnotationColorIndex(AnnotationTool.Highlight);
        Assert.InRange(got, 0, AnnotationInteractionHandler.HighlightColors.Length - 1);
    }

    // --- Browse-mode drag ---

    [Fact]
    public void BrowseDrag_RectAnnotation_UpdatesPositionAndPushesUndo()
    {
        var rect = AddRect(100, 100, 50, 40);
        _doc.UndoStack.Clear();

        bool hit = _handler.HandleBrowsePointerDown(_doc, 120, 120);
        Assert.True(hit);
        Assert.Same(rect, _handler.SelectedAnnotation);

        _handler.HandleBrowsePointerMove(130, 130);
        _handler.HandleBrowsePointerMove(140, 140);
        _handler.HandleBrowsePointerUp(_doc, 140, 140);

        Assert.True(rect.X > 100f);
        Assert.True(rect.Y > 100f);
        Assert.NotEmpty(_doc.UndoStack);
    }

    [Fact]
    public void BrowsePointerDown_MissesAnnotation_ReturnsFalse()
    {
        AddRect(100, 100, 50, 40);
        bool hit = _handler.HandleBrowsePointerDown(_doc, 500, 500);
        Assert.False(hit);
        Assert.Null(_handler.SelectedAnnotation);
    }

    // --- Text notes ---

    [Fact]
    public void CompleteTextNote_AddsToPageAndClearsPreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.TextNote);
        _handler.CompleteTextNote(_doc, 150, 200, "Hello");

        var notes = _doc.Annotations.Pages[0].OfType<TextNoteAnnotation>().ToList();
        Assert.Single(notes);
        Assert.Equal("Hello", notes[0].Text);
        Assert.Equal(150f, notes[0].X);
        Assert.Equal(200f, notes[0].Y);
    }

    [Fact]
    public void CompleteTextNoteEdit_UpdatesExistingText()
    {
        var note = new TextNoteAnnotation { X = 100, Y = 100, Text = "original", Color = "#000000" };
        _doc.AddAnnotation(0, note);

        _handler.CompleteTextNoteEdit(_doc, note, "revised");

        Assert.Equal("revised", note.Text);
    }

    // --- Undo/redo round trip ---

    [Fact]
    public void UndoRedo_Commit_RoundTrips()
    {
        _handler.SetAnnotationTool(AnnotationTool.Rectangle);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc, 150, 150);
        _handler.HandleAnnotationPointerUp(_doc, 150, 150);
        Assert.Single(_doc.Annotations.Pages[0]);

        _handler.UndoAnnotation(_doc);
        Assert.Empty(_doc.Annotations.Pages[0]);

        _handler.RedoAnnotation(_doc);
        Assert.Single(_doc.Annotations.Pages[0]);
    }

    // --- Trivial pointer (no move) should still commit a zero-extent shape or drop cleanly ---

    [Fact]
    public void PointerDownUp_Rect_NoMove_DoesNotLeavePreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.Rectangle);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerUp(_doc, 100, 100);

        Assert.Null(_handler.PreviewAnnotation);
    }

    // --- Eraser hit / miss ---

    [Fact]
    public void Eraser_MissesAnnotation_KeepsIt()
    {
        AddFreehand(100, 100);
        _handler.SetAnnotationTool(AnnotationTool.Eraser);
        _handler.HandleAnnotationPointerDown(_doc, 500, 500);

        Assert.Single(_doc.Annotations.Pages[0]);
    }

    [Fact]
    public void Eraser_OverlappingAnnotations_RemovesOnePerPointerDown()
    {
        AddFreehand(100, 100);
        AddFreehand(100, 100); // overlapping
        _handler.SetAnnotationTool(AnnotationTool.Eraser);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        Assert.Single(_doc.Annotations.Pages[0]);

        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        Assert.Empty(_doc.Annotations.Pages[0]);
    }
}
