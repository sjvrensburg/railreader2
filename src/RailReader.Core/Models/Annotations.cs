using System.Text.Json.Serialization;

namespace RailReader.Core.Models;

public class BookmarkEntry
{
    public string Name { get; set; } = "";
    public int Page { get; set; }

    [JsonIgnore]
    public string PageDisplay => $"Page {Page + 1}";
}

public class AnnotationFile
{
    public int Version { get; set; } = 1;
    public string SourcePdf { get; set; } = "";
    /// <summary>Full path to the source PDF. Used for orphan detection in internal storage.</summary>
    public string SourcePdfPath { get; set; } = "";
    public Dictionary<int, List<Annotation>> Pages { get; } = [];
    public List<BookmarkEntry> Bookmarks { get; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HighlightAnnotation), "highlight")]
[JsonDerivedType(typeof(FreehandAnnotation), "freehand")]
[JsonDerivedType(typeof(TextNoteAnnotation), "text_note")]
[JsonDerivedType(typeof(RectAnnotation), "rect")]
public abstract class Annotation
{
    public string Color { get; set; } = "#FFFF00";
    public float Opacity { get; set; } = 1.0f;
}

public class HighlightAnnotation : Annotation
{
    public List<HighlightRect> Rects { get; set; } = [];
}

public record struct HighlightRect(float X, float Y, float W, float H);

public class FreehandAnnotation : Annotation
{
    public float StrokeWidth { get; set; } = 2f;
    public List<PointF> Points { get; set; } = [];
}

public record struct PointF(float X, float Y);

public class TextNoteAnnotation : Annotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public string Text { get; set; } = "";

    [JsonIgnore]
    public bool IsExpanded { get; set; }
}

public class RectAnnotation : Annotation
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public float StrokeWidth { get; set; } = 2f;
    public bool Filled { get; set; }
}

public interface IUndoAction
{
    void Undo(AnnotationFile file);
    void Redo(AnnotationFile file);
}

public class AddAnnotationAction : IUndoAction
{
    private readonly int _pageIndex;
    private readonly Annotation _annotation;

    public AddAnnotationAction(int pageIndex, Annotation annotation)
    {
        _pageIndex = pageIndex;
        _annotation = annotation;
    }

    public void Undo(AnnotationFile file)
    {
        if (file.Pages.TryGetValue(_pageIndex, out var list))
            list.Remove(_annotation);
    }

    public void Redo(AnnotationFile file)
    {
        if (!file.Pages.TryGetValue(_pageIndex, out var list))
        {
            list = [];
            file.Pages[_pageIndex] = list;
        }
        list.Add(_annotation);
    }
}

public class RemoveAnnotationAction : IUndoAction
{
    private readonly int _pageIndex;
    private readonly Annotation _annotation;
    private int _index;

    public RemoveAnnotationAction(int pageIndex, Annotation annotation)
    {
        _pageIndex = pageIndex;
        _annotation = annotation;
    }

    public void Undo(AnnotationFile file)
    {
        if (!file.Pages.TryGetValue(_pageIndex, out var list))
        {
            list = [];
            file.Pages[_pageIndex] = list;
        }
        list.Insert(Math.Min(_index, list.Count), _annotation);
    }

    public void Redo(AnnotationFile file)
    {
        if (file.Pages.TryGetValue(_pageIndex, out var list))
        {
            _index = list.IndexOf(_annotation);
            list.Remove(_annotation);
        }
    }
}

public class MoveAnnotationAction : IUndoAction
{
    private readonly Annotation _annotation;
    private readonly PositionSnapshot _oldPosition;
    private readonly PositionSnapshot _newPosition;

    public MoveAnnotationAction(Annotation annotation, PositionSnapshot oldPosition, PositionSnapshot newPosition)
    {
        _annotation = annotation;
        _oldPosition = oldPosition;
        _newPosition = newPosition;
    }

    public void Undo(AnnotationFile file) => _oldPosition.ApplyTo(_annotation);
    public void Redo(AnnotationFile file) => _newPosition.ApplyTo(_annotation);
}

public class PositionSnapshot
{
    public float X { get; init; }
    public float Y { get; init; }
    public List<PointF>? Points { get; init; }
    public List<HighlightRect>? Rects { get; init; }

    public static PositionSnapshot Capture(Annotation annotation) => annotation switch
    {
        TextNoteAnnotation tn => new() { X = tn.X, Y = tn.Y },
        FreehandAnnotation f => new() { Points = [.. f.Points] },
        HighlightAnnotation h => new() { Rects = [.. h.Rects] },
        RectAnnotation r => new() { X = r.X, Y = r.Y },
        _ => new(),
    };

    public void ApplyTo(Annotation annotation)
    {
        switch (annotation)
        {
            case TextNoteAnnotation tn:
                tn.X = X; tn.Y = Y;
                break;
            case FreehandAnnotation f when Points is not null:
                f.Points = [.. Points];
                break;
            case HighlightAnnotation h when Rects is not null:
                h.Rects = [.. Rects];
                break;
            case RectAnnotation r:
                r.X = X; r.Y = Y;
                break;
        }
    }
}

public enum ResizeHandle
{
    None,
    TopLeft, Top, TopRight,
    Right,
    BottomRight, Bottom, BottomLeft,
    Left,
}

public class ResizeFreehandAction : IUndoAction
{
    private readonly FreehandAnnotation _annotation;
    private readonly List<PointF> _oldPoints;
    private readonly List<PointF> _newPoints;

    public ResizeFreehandAction(FreehandAnnotation annotation, List<PointF> oldPoints, List<PointF> newPoints)
    {
        _annotation = annotation;
        _oldPoints = oldPoints;
        _newPoints = newPoints;
    }

    public void Undo(AnnotationFile file) => _annotation.Points = [.. _oldPoints];
    public void Redo(AnnotationFile file) => _annotation.Points = [.. _newPoints];
}
