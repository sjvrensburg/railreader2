using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

/// <summary>
/// Shared annotation drawing logic used by both the overlay layer and the export path.
/// </summary>
public static class AnnotationRenderer
{
    // Cached paints — per render thread via [ThreadStatic].
    // Color/StrokeWidth are mutated per annotation; Style/StrokeCap/etc. are stable.
    [ThreadStatic] private static SKPaint? s_fillPaint;
    [ThreadStatic] private static SKPaint? s_strokePaint;
    [ThreadStatic] private static SKPaint? s_noteBorderPaint;
    [ThreadStatic] private static SKPaint? s_noteTextPaint;
    [ThreadStatic] private static SKPaint? s_noteBgPaint;
    [ThreadStatic] private static SKFont? s_noteFont;
    [ThreadStatic] private static SKPaint? s_dashPaint;

    public static void DrawAnnotations(SKCanvas canvas, List<Annotation> annotations, Annotation? selected)
    {
        foreach (var ann in annotations)
            DrawAnnotation(canvas, ann, ann == selected);
    }

    public static void DrawAnnotation(SKCanvas canvas, Annotation annotation, bool isSelected)
    {
        var color = ParseColor(annotation.Color, annotation.Opacity);

        switch (annotation)
        {
            case HighlightAnnotation highlight:
                DrawHighlight(canvas, highlight, color);
                break;
            case FreehandAnnotation freehand:
                DrawFreehand(canvas, freehand, color);
                break;
            case TextNoteAnnotation textNote:
                DrawTextNote(canvas, textNote, color);
                break;
            case RectAnnotation rect:
                DrawRect(canvas, rect, color);
                break;
        }

        if (isSelected)
            DrawSelectionIndicator(canvas, annotation);
    }

    private static SKPaint GetFillPaint()
        => s_fillPaint ??= new SKPaint { IsAntialias = true };

    private static SKPaint GetStrokePaint()
        => s_strokePaint ??= new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

    private static void DrawHighlight(SKCanvas canvas, HighlightAnnotation highlight, SKColor color)
    {
        var paint = GetFillPaint();
        paint.Color = color;
        foreach (var r in highlight.Rects)
            canvas.DrawRect(SKRect.Create(r.X, r.Y, r.W, r.H), paint);
    }

    private static void DrawFreehand(SKCanvas canvas, FreehandAnnotation freehand, SKColor color)
    {
        if (freehand.Points.Count < 2) return;

        using var path = new SKPath();
        path.MoveTo(freehand.Points[0].X, freehand.Points[0].Y);
        for (int i = 1; i < freehand.Points.Count; i++)
            path.LineTo(freehand.Points[i].X, freehand.Points[i].Y);

        var paint = GetStrokePaint();
        paint.Color = color;
        paint.StrokeWidth = freehand.StrokeWidth;
        canvas.DrawPath(path, paint);
    }

    private static void DrawTextNote(SKCanvas canvas, TextNoteAnnotation note, SKColor color)
    {
        float size = 12f;
        var markerRect = SKRect.Create(note.X - size / 2, note.Y - size / 2, size, size);

        var fillPaint = GetFillPaint();
        fillPaint.Color = color;
        canvas.DrawRect(markerRect, fillPaint);

        var borderPaint = s_noteBorderPaint ??= new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
        };
        borderPaint.Color = color.WithAlpha(255);
        canvas.DrawRect(markerRect, borderPaint);

        if (!string.IsNullOrEmpty(note.Text))
        {
            var font = s_noteFont ??= new SKFont(SKTypeface.Default, 9);
            var textPaint = s_noteTextPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 220), IsAntialias = true };
            var bgPaint = s_noteBgPaint ??= new SKPaint { Color = new SKColor(255, 255, 200, 230) };
            float textWidth = font.MeasureText(note.Text);
            var bgRect = SKRect.Create(note.X + size / 2 + 2, note.Y - 6, textWidth + 6, 14);
            canvas.DrawRect(bgRect, bgPaint);
            canvas.DrawText(note.Text, note.X + size / 2 + 5, note.Y + 4, font, textPaint);
        }
    }

    private static void DrawRect(SKCanvas canvas, RectAnnotation rect, SKColor color)
    {
        var skRect = SKRect.Create(rect.X, rect.Y, rect.W, rect.H);
        if (rect.Filled)
        {
            var fillPaint = GetFillPaint();
            fillPaint.Color = color;
            canvas.DrawRect(skRect, fillPaint);
        }
        var strokePaint = GetStrokePaint();
        strokePaint.Color = color.WithAlpha(255);
        strokePaint.StrokeWidth = rect.StrokeWidth;
        canvas.DrawRect(skRect, strokePaint);
    }

    private static void DrawSelectionIndicator(SKCanvas canvas, Annotation annotation)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return;

        var dashPaint = s_dashPaint ??= new SKPaint
        {
            Color = new SKColor(0, 120, 212, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
            IsAntialias = true,
        };
        var selRect = SKRect.Create(rect.Left - 3, rect.Top - 3, rect.Width + 6, rect.Height + 6);
        canvas.DrawRect(selRect, dashPaint);
    }

    public static SKRect? GetAnnotationBounds(Annotation annotation)
    {
        switch (annotation)
        {
            case HighlightAnnotation h when h.Rects.Count > 0:
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var r in h.Rects)
                {
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.X + r.W > maxX) maxX = r.X + r.W;
                    if (r.Y + r.H > maxY) maxY = r.Y + r.H;
                }
                return new SKRect(minX, minY, maxX, maxY);
            case FreehandAnnotation f when f.Points.Count > 0:
                float fMinX = float.MaxValue, fMinY = float.MaxValue;
                float fMaxX = float.MinValue, fMaxY = float.MinValue;
                foreach (var p in f.Points)
                {
                    if (p.X < fMinX) fMinX = p.X;
                    if (p.Y < fMinY) fMinY = p.Y;
                    if (p.X > fMaxX) fMaxX = p.X;
                    if (p.Y > fMaxY) fMaxY = p.Y;
                }
                return new SKRect(fMinX, fMinY, fMaxX, fMaxY);
            case TextNoteAnnotation t:
                return new SKRect(t.X - 6, t.Y - 6, t.X + 6, t.Y + 6);
            case RectAnnotation r:
                return new SKRect(r.X, r.Y, r.X + r.W, r.Y + r.H);
            default:
                return null;
        }
    }

    public static bool HitTest(Annotation annotation, float pageX, float pageY, float tolerance = 4f)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return false;
        rect.Inflate(tolerance, tolerance);
        return rect.Contains(pageX, pageY);
    }

    private static SKColor ParseColor(string hex, float opacity)
    {
        byte a = (byte)(opacity * 255);
        if (hex.Length == 7 && hex[0] == '#')
        {
            byte r = Convert.ToByte(hex[1..3], 16);
            byte g = Convert.ToByte(hex[3..5], 16);
            byte b = Convert.ToByte(hex[5..7], 16);
            return new SKColor(r, g, b, a);
        }
        return new SKColor(255, 255, 0, a); // fallback yellow
    }
}
