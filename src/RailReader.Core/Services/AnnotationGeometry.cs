using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Pure-geometry annotation operations (hit testing, bounds computation).
/// No rendering-library dependency — used by AnnotationInteractionHandler in Core.
/// </summary>
public static class AnnotationGeometry
{
    public const float NoteIconSize = 16f;

    public static RectF? GetAnnotationBounds(Annotation annotation)
    {
        switch (annotation)
        {
            case HighlightAnnotation h when h.Rects.Count > 0:
                return ComputeRectsBounds(h.Rects);
            case FreehandAnnotation f when f.Points.Count > 0:
                return ComputePointsBounds(f.Points);
            case TextNoteAnnotation t:
                float half = NoteIconSize / 2;
                return new RectF(t.X - half, t.Y - half, t.X + half, t.Y + half);
            case RectAnnotation r:
                return new RectF(r.X, r.Y, r.X + r.W, r.Y + r.H);
            default:
                return null;
        }
    }

    public static RectF ComputeRectsBounds(List<HighlightRect> rects)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var r in rects)
        {
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.W > maxX) maxX = r.X + r.W;
            if (r.Y + r.H > maxY) maxY = r.Y + r.H;
        }
        return new RectF(minX, minY, maxX, maxY);
    }

    public static RectF ComputePointsBounds(List<PointF> points)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new RectF(minX, minY, maxX, maxY);
    }

    public static bool HitTest(Annotation annotation, float pageX, float pageY, float tolerance = 4f)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return false;
        rect.Inflate(tolerance, tolerance);
        return rect.Contains(pageX, pageY);
    }

    public static ResizeHandle HitTestResizeHandle(FreehandAnnotation annotation, float pageX, float pageY, float tolerance = 8f)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return ResizeHandle.None;

        var selRect = new RectF(rect.Left - 3, rect.Top - 3, rect.Right + 3, rect.Bottom + 3);
        var positions = GetHandlePositions(selRect);

        for (int i = 0; i < positions.Length; i++)
        {
            float dx = pageX - positions[i].X;
            float dy = pageY - positions[i].Y;
            if (dx * dx + dy * dy <= tolerance * tolerance)
                return (ResizeHandle)(i + 1);
        }
        return ResizeHandle.None;
    }

    public static (float X, float Y)[] GetHandlePositions(RectF bounds)
    {
        float cx = bounds.MidX, cy = bounds.MidY;
        return
        [
            (bounds.Left, bounds.Top),
            (cx, bounds.Top),
            (bounds.Right, bounds.Top),
            (bounds.Right, cy),
            (bounds.Right, bounds.Bottom),
            (cx, bounds.Bottom),
            (bounds.Left, bounds.Bottom),
            (bounds.Left, cy),
        ];
    }

    public static RectF ComputeNewBounds(RectF old, ResizeHandle handle, float px, float py, float startX, float startY)
    {
        float dx = px - startX;
        float dy = py - startY;
        float l = old.Left, t = old.Top, r = old.Right, b = old.Bottom;

        switch (handle)
        {
            case ResizeHandle.TopLeft:     l += dx; t += dy; break;
            case ResizeHandle.Top:         t += dy; break;
            case ResizeHandle.TopRight:    r += dx; t += dy; break;
            case ResizeHandle.Right:       r += dx; break;
            case ResizeHandle.BottomRight: r += dx; b += dy; break;
            case ResizeHandle.Bottom:      b += dy; break;
            case ResizeHandle.BottomLeft:  l += dx; b += dy; break;
            case ResizeHandle.Left:        l += dx; break;
        }

        return new RectF(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }
}
