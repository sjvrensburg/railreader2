using System.Text.Json.Serialization;

namespace RailReader2.Models;

public class AnnotationFile
{
    public int Version { get; set; } = 1;
    public string SourcePdf { get; set; } = "";
    public Dictionary<int, List<Annotation>> Pages { get; set; } = [];
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

// Undo system
public interface IUndoAction
{
    void Undo(AnnotationFile file, int pageIndex);
    void Redo(AnnotationFile file, int pageIndex);
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

    public void Undo(AnnotationFile file, int pageIndex)
    {
        if (file.Pages.TryGetValue(_pageIndex, out var list))
            list.Remove(_annotation);
    }

    public void Redo(AnnotationFile file, int pageIndex)
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

    public void Undo(AnnotationFile file, int pageIndex)
    {
        if (!file.Pages.TryGetValue(_pageIndex, out var list))
        {
            list = [];
            file.Pages[_pageIndex] = list;
        }
        list.Insert(Math.Min(_index, list.Count), _annotation);
    }

    public void Redo(AnnotationFile file, int pageIndex)
    {
        if (file.Pages.TryGetValue(_pageIndex, out var list))
        {
            _index = list.IndexOf(_annotation);
            list.Remove(_annotation);
        }
    }
}
