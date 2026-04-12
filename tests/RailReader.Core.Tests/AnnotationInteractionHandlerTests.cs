using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AnnotationInteractionHandlerTests : IDisposable
{
    private readonly DocumentState _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler;

    public AnnotationInteractionHandlerTests()
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

    [Fact]
    public void SetTool_Pen_ActivatesTool()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);

        Assert.True(_handler.IsAnnotating);
        Assert.Equal(AnnotationTool.Pen, _handler.ActiveTool);
    }

    [Fact]
    public void SetTool_None_DeactivatesTool()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.SetAnnotationTool(AnnotationTool.None);

        Assert.False(_handler.IsAnnotating);
        Assert.Equal(AnnotationTool.None, _handler.ActiveTool);
    }

    [Fact]
    public void SetTool_Highlight_SetsHighlightColor()
    {
        _handler.SetAnnotationTool(AnnotationTool.Highlight);

        Assert.Equal("#FFFF00", _handler.ActiveAnnotationColor);
    }

    [Fact]
    public void SetTool_Pen_SetsPenColor()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);

        Assert.Equal("#FF0000", _handler.ActiveAnnotationColor);
    }

    [Fact]
    public void PointerDown_Pen_StartsPreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);

        _handler.HandleAnnotationPointerDown(_doc, 100, 100);

        // Pen preview is created on PointerMove (PointerDown just records the start point),
        // but we can trigger it by moving immediately after.
        _handler.HandleAnnotationPointerMove(_doc, 101, 101);

        Assert.NotNull(_handler.PreviewAnnotation);
        Assert.IsType<FreehandAnnotation>(_handler.PreviewAnnotation);
    }

    [Fact]
    public void PointerMove_Pen_UpdatesPreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);

        _handler.HandleAnnotationPointerMove(_doc, 110, 110);
        _handler.HandleAnnotationPointerMove(_doc, 120, 120);
        _handler.HandleAnnotationPointerMove(_doc, 130, 130);

        var preview = Assert.IsType<FreehandAnnotation>(_handler.PreviewAnnotation);
        Assert.True(preview.Points.Count >= 3);
    }

    [Fact]
    public void PointerUp_Pen_CommitsAnnotation()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc, 110, 110);
        _handler.HandleAnnotationPointerMove(_doc, 120, 120);

        _handler.HandleAnnotationPointerUp(_doc, 120, 120);

        Assert.Null(_handler.PreviewAnnotation);
        Assert.True(_doc.Annotations.Pages.ContainsKey(0));
        Assert.Single(_doc.Annotations.Pages[0]);
        Assert.IsType<FreehandAnnotation>(_doc.Annotations.Pages[0][0]);
    }

    [Fact]
    public void PointerDown_Rect_StartsPreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.Rectangle);

        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc, 150, 150);

        Assert.NotNull(_handler.PreviewAnnotation);
        Assert.IsType<RectAnnotation>(_handler.PreviewAnnotation);
    }

    [Fact]
    public void PointerUp_Rect_CommitsAnnotation()
    {
        _handler.SetAnnotationTool(AnnotationTool.Rectangle);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc, 150, 150);

        _handler.HandleAnnotationPointerUp(_doc, 150, 150);

        Assert.Null(_handler.PreviewAnnotation);
        Assert.True(_doc.Annotations.Pages.ContainsKey(0));
        Assert.Single(_doc.Annotations.Pages[0]);
        Assert.IsType<RectAnnotation>(_doc.Annotations.Pages[0][0]);
    }

    [Fact]
    public void Eraser_RemovesAnnotation()
    {
        // Add a freehand annotation at a known location
        var freehand = new FreehandAnnotation
        {
            Points = [new PointF(95, 95), new PointF(100, 100), new PointF(105, 105)],
            Color = "#FF0000",
            Opacity = 0.8f,
            StrokeWidth = 2f,
        };
        _doc.AddAnnotation(0, freehand);
        Assert.Single(_doc.Annotations.Pages[0]);

        // Erase at the annotation's location
        _handler.SetAnnotationTool(AnnotationTool.Eraser);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);

        Assert.Empty(_doc.Annotations.Pages[0]);
    }

    [Fact]
    public void DeleteSelected_RemovesAndPushesUndo()
    {
        var freehand = new FreehandAnnotation
        {
            Points = [new PointF(95, 95), new PointF(100, 100), new PointF(105, 105)],
            Color = "#FF0000",
            Opacity = 0.8f,
            StrokeWidth = 2f,
        };
        _doc.AddAnnotation(0, freehand);

        // Clear undo stack so we can verify the delete pushes a new action
        _doc.UndoStack.Clear();

        _handler.SelectedAnnotation = freehand;
        bool deleted = _handler.DeleteSelectedAnnotation(_doc);

        Assert.True(deleted);
        Assert.Empty(_doc.Annotations.Pages[0]);
        Assert.Null(_handler.SelectedAnnotation);
        Assert.NotEmpty(_doc.UndoStack);
    }

    [Fact]
    public void CycleHighlightColor_ChangesActiveColor()
    {
        // Start with default highlight color (index 0 = yellow)
        _handler.SetAnnotationTool(AnnotationTool.Highlight);
        string firstColor = _handler.ActiveAnnotationColor;

        // Change to index 1 (green) and re-apply tool to update active color
        _handler.SetAnnotationColorIndex(AnnotationTool.Highlight, 1);
        _handler.SetAnnotationTool(AnnotationTool.Highlight);
        string secondColor = _handler.ActiveAnnotationColor;

        // Change to index 2 (pink) and re-apply
        _handler.SetAnnotationColorIndex(AnnotationTool.Highlight, 2);
        _handler.SetAnnotationTool(AnnotationTool.Highlight);
        string thirdColor = _handler.ActiveAnnotationColor;

        Assert.NotEqual(firstColor, secondColor);
        Assert.NotEqual(secondColor, thirdColor);
        Assert.Equal("#FFFF00", firstColor);
        Assert.Equal("#66CC66", secondColor);
        Assert.Equal("#FF8FA0", thirdColor);
    }

    [Fact]
    public void ShiftHeld_Pen_ConstrainsStraightLine()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);

        // Draw several moves with Shift held
        _handler.HandleAnnotationPointerMove(_doc, 110, 115, shiftHeld: true);
        _handler.HandleAnnotationPointerMove(_doc, 120, 130, shiftHeld: true);
        _handler.HandleAnnotationPointerMove(_doc, 150, 160, shiftHeld: true);

        var preview = Assert.IsType<FreehandAnnotation>(_handler.PreviewAnnotation);
        // Shift constrains to 2 points: start + current
        Assert.Equal(2, preview.Points.Count);
        Assert.Equal(100f, preview.Points[0].X);
        Assert.Equal(100f, preview.Points[0].Y);
        Assert.Equal(150f, preview.Points[1].X);
        Assert.Equal(160f, preview.Points[1].Y);
    }

    [Fact]
    public void ShiftReleased_Pen_ResumesFreehand()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);

        // Start with Shift held
        _handler.HandleAnnotationPointerMove(_doc, 110, 110, shiftHeld: true);
        var constrained = Assert.IsType<FreehandAnnotation>(_handler.PreviewAnnotation);
        Assert.Equal(2, constrained.Points.Count);

        // Release Shift — freehand resumes with all accumulated points
        _handler.HandleAnnotationPointerMove(_doc, 120, 120, shiftHeld: false);
        _handler.HandleAnnotationPointerMove(_doc, 130, 130, shiftHeld: false);
        var freehand = Assert.IsType<FreehandAnnotation>(_handler.PreviewAnnotation);
        Assert.True(freehand.Points.Count > 2);
    }

    [Fact]
    public void ShiftHeld_Pen_CommitsStraightLine()
    {
        _handler.SetAnnotationTool(AnnotationTool.Pen);
        _handler.HandleAnnotationPointerDown(_doc, 100, 100);

        _handler.HandleAnnotationPointerMove(_doc, 150, 200, shiftHeld: true);
        _handler.HandleAnnotationPointerUp(_doc, 150, 200);

        Assert.Null(_handler.PreviewAnnotation);
        var committed = Assert.IsType<FreehandAnnotation>(_doc.Annotations.Pages[0][0]);
        Assert.Equal(2, committed.Points.Count);
    }
}
