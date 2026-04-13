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

public sealed class AddAnnotationAction(int pageIndex, Annotation annotation) : IUndoAction
{
    public void Undo(AnnotationFile file)
    {
        if (file.Pages.TryGetValue(pageIndex, out var list))
            list.Remove(annotation);
    }

    public void Redo(AnnotationFile file)
    {
        if (!file.Pages.TryGetValue(pageIndex, out var list))
        {
            list = [];
            file.Pages[pageIndex] = list;
        }
        list.Add(annotation);
    }
}

public sealed class RemoveAnnotationAction(int pageIndex, Annotation annotation) : IUndoAction
{
    private int _index;

    public void Undo(AnnotationFile file)
    {
        if (!file.Pages.TryGetValue(pageIndex, out var list))
        {
            list = [];
            file.Pages[pageIndex] = list;
        }
        list.Insert(Math.Min(_index, list.Count), annotation);
    }

    public void Redo(AnnotationFile file)
    {
        if (file.Pages.TryGetValue(pageIndex, out var list))
        {
            _index = list.IndexOf(annotation);
            list.Remove(annotation);
        }
    }
}

public sealed class MoveAnnotationAction(
    Annotation annotation, PositionSnapshot oldPosition, PositionSnapshot newPosition) : IUndoAction
{
    public void Undo(AnnotationFile file) => oldPosition.ApplyTo(annotation);
    public void Redo(AnnotationFile file) => newPosition.ApplyTo(annotation);
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

public sealed class ResizeFreehandAction(
    FreehandAnnotation annotation, List<PointF> oldPoints, List<PointF> newPoints) : IUndoAction
{
    public void Undo(AnnotationFile file) => annotation.Points = [.. oldPoints];
    public void Redo(AnnotationFile file) => annotation.Points = [.. newPoints];
}
