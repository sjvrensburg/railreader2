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
    [ThreadStatic] private static SKPaint? s_handleFillPaint;
    [ThreadStatic] private static SKPaint? s_handleStrokePaint;

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

    private const float NoteIconSize = 16f;
    private const float PopupMaxWidth = 200f;
    private const float PopupPadding = 6f;

    private static void DrawTextNote(SKCanvas canvas, TextNoteAnnotation note, SKColor color)
    {
        float ix = note.X - NoteIconSize / 2;
        float iy = note.Y - NoteIconSize / 2;
        float foldSize = 4f;

        // Icon body (folded corner note shape)
        using var iconPath = new SKPath();
        iconPath.MoveTo(ix, iy);
        iconPath.LineTo(ix + NoteIconSize - foldSize, iy);
        iconPath.LineTo(ix + NoteIconSize, iy + foldSize);
        iconPath.LineTo(ix + NoteIconSize, iy + NoteIconSize);
        iconPath.LineTo(ix, iy + NoteIconSize);
        iconPath.Close();

        var fillPaint = GetFillPaint();
        fillPaint.Color = color;
        canvas.DrawPath(iconPath, fillPaint);

        // Fold triangle
        using var foldPath = new SKPath();
        foldPath.MoveTo(ix + NoteIconSize - foldSize, iy);
        foldPath.LineTo(ix + NoteIconSize - foldSize, iy + foldSize);
        foldPath.LineTo(ix + NoteIconSize, iy + foldSize);
        foldPath.Close();
        var foldColor = color.WithAlpha((byte)(color.Alpha * 0.6f));
        fillPaint.Color = foldColor;
        canvas.DrawPath(foldPath, fillPaint);

        // Border
        var borderPaint = s_noteBorderPaint ??= new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
        };
        borderPaint.Color = color.WithAlpha(255);
        canvas.DrawPath(iconPath, borderPaint);

        // Expanded popup
        if (note.IsExpanded && !string.IsNullOrEmpty(note.Text))
        {
            var font = s_noteFont ??= new SKFont(SKTypeface.Default, 9);
            var textPaint = s_noteTextPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 220), IsAntialias = true };
            var bgPaint = s_noteBgPaint ??= new SKPaint { Color = new SKColor(255, 255, 200, 240) };

            // Word-wrap text
            var lines = WrapText(note.Text, font, PopupMaxWidth - PopupPadding * 2);
            float lineHeight = font.Size * 1.4f;
            float popupW = PopupPadding * 2;
            foreach (var line in lines)
            {
                float lw = font.MeasureText(line);
                if (lw + PopupPadding * 2 > popupW) popupW = lw + PopupPadding * 2;
            }
            popupW = Math.Min(popupW, PopupMaxWidth);
            float popupH = lines.Count * lineHeight + PopupPadding * 2;

            float popupX = note.X + NoteIconSize / 2 + 4;
            float popupY = note.Y + NoteIconSize / 2 + 2;

            // Shadow
            using var shadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 40),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(SKRect.Create(popupX + 1, popupY + 1, popupW, popupH), 3, 3, shadowPaint);

            // Popup background
            var popupRect = SKRect.Create(popupX, popupY, popupW, popupH);
            canvas.DrawRoundRect(popupRect, 3, 3, bgPaint);

            // Popup border
            borderPaint.Color = new SKColor(180, 170, 100, 200);
            canvas.DrawRoundRect(popupRect, 3, 3, borderPaint);

            // Text lines
            float ty = popupY + PopupPadding + font.Size;
            foreach (var line in lines)
            {
                canvas.DrawText(line, popupX + PopupPadding, ty, font, textPaint);
                ty += lineHeight;
            }
        }
    }

    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (string.IsNullOrEmpty(paragraph)) { lines.Add(""); continue; }

            var words = paragraph.Split(' ');
            string current = "";
            foreach (var word in words)
            {
                string test = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(test) > maxWidth && current.Length > 0)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = test;
                }
            }
            if (current.Length > 0) lines.Add(current);
        }
        return lines.Count > 0 ? lines : [""];
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

        // Draw resize handles for freehand annotations
        if (annotation is FreehandAnnotation)
            DrawResizeHandles(canvas, selRect);
    }

    private static void DrawResizeHandles(SKCanvas canvas, SKRect bounds)
    {
        const float size = 6f;
        const float half = size / 2;

        var fill = s_handleFillPaint ??= new SKPaint { Color = SKColors.White, IsAntialias = true };
        var stroke = s_handleStrokePaint ??= new SKPaint
        {
            Color = new SKColor(0, 120, 212, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };

        var positions = GetHandlePositions(bounds);
        foreach (var (hx, hy) in positions)
        {
            var handleRect = SKRect.Create(hx - half, hy - half, size, size);
            canvas.DrawRect(handleRect, fill);
            canvas.DrawRect(handleRect, stroke);
        }
    }

    private static (float X, float Y)[] GetHandlePositions(SKRect bounds)
    {
        float cx = bounds.MidX, cy = bounds.MidY;
        return
        [
            (bounds.Left, bounds.Top),      // TopLeft
            (cx, bounds.Top),               // Top
            (bounds.Right, bounds.Top),     // TopRight
            (bounds.Right, cy),             // Right
            (bounds.Right, bounds.Bottom),  // BottomRight
            (cx, bounds.Bottom),            // Bottom
            (bounds.Left, bounds.Bottom),   // BottomLeft
            (bounds.Left, cy),              // Left
        ];
    }

    public static ResizeHandle HitTestResizeHandle(FreehandAnnotation annotation, float pageX, float pageY, float tolerance = 8f)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return ResizeHandle.None;

        var selRect = SKRect.Create(rect.Left - 3, rect.Top - 3, rect.Width + 6, rect.Height + 6);
        var positions = GetHandlePositions(selRect);

        // ResizeHandle enum: None=0, TopLeft=1..Left=8
        for (int i = 0; i < positions.Length; i++)
        {
            float dx = pageX - positions[i].X;
            float dy = pageY - positions[i].Y;
            if (dx * dx + dy * dy <= tolerance * tolerance)
                return (ResizeHandle)(i + 1);
        }
        return ResizeHandle.None;
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
                return ComputePointsBounds(f.Points);
            case TextNoteAnnotation t:
                float half = NoteIconSize / 2;
                return new SKRect(t.X - half, t.Y - half, t.X + half, t.Y + half);
            case RectAnnotation r:
                return new SKRect(r.X, r.Y, r.X + r.W, r.Y + r.H);
            default:
                return null;
        }
    }

    private static SKRect ComputePointsBounds(List<PointF> points)
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
        return new SKRect(minX, minY, maxX, maxY);
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
