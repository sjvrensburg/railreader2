using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.Models;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class AnnotationLayer : Control
{
    public TabViewModel? Tab { get; set; }
    public MainWindowViewModel? ViewModel { get; set; }

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var tab = Tab;
        var vm = ViewModel;
        if (tab is null || vm is null) return;

        double w = tab.PageWidth > 0 ? tab.PageWidth : Bounds.Width;
        double h = tab.PageHeight > 0 ? tab.PageHeight : Bounds.Height;
        context.Custom(new AnnotationDrawOperation(new Rect(0, 0, w, h), tab, vm));
    }

    private sealed class AnnotationDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TabViewModel _tab;
        private readonly MainWindowViewModel _vm;

        public AnnotationDrawOperation(Rect bounds, TabViewModel tab, MainWindowViewModel vm)
        {
            _bounds = bounds;
            _tab = tab;
            _vm = vm;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var annotations = _tab.Annotations;
            if (annotations is not null &&
                annotations.Pages.TryGetValue(_tab.CurrentPage, out var pageAnnotations))
            {
                AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, _vm.SelectedAnnotation);
            }

            // Draw in-progress annotation preview
            var preview = _vm.PreviewAnnotation;
            if (preview is not null)
                AnnotationRenderer.DrawAnnotation(canvas, preview, false);

            // Draw text selection rects (blue semi-transparent)
            var selRects = _vm.TextSelectionRects;
            if (selRects is { Count: > 0 })
            {
                using var selPaint = new SKPaint
                {
                    Color = new SKColor(0x33, 0x90, 0xFF, 77), // ~30% opacity
                    IsAntialias = true,
                };
                foreach (var r in selRects)
                    canvas.DrawRect(SKRect.Create(r.X, r.Y, r.W, r.H), selPaint);
            }
        }
    }
}

/// <summary>
/// Shared annotation drawing logic used by both the overlay layer and the export path.
/// </summary>
public static class AnnotationRenderer
{
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

    private static void DrawHighlight(SKCanvas canvas, HighlightAnnotation highlight, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
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

        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = freehand.StrokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };
        canvas.DrawPath(path, paint);
    }

    private static void DrawTextNote(SKCanvas canvas, TextNoteAnnotation note, SKColor color)
    {
        // Draw marker icon
        float size = 12f;
        using var markerPaint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRect(SKRect.Create(note.X - size / 2, note.Y - size / 2, size, size), markerPaint);

        // Draw border
        using var borderPaint = new SKPaint
        {
            Color = color.WithAlpha(255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
        };
        canvas.DrawRect(SKRect.Create(note.X - size / 2, note.Y - size / 2, size, size), borderPaint);

        // Draw text label
        if (!string.IsNullOrEmpty(note.Text))
        {
            using var font = new SKFont(SKTypeface.Default, 9);
            using var textPaint = new SKPaint { Color = new SKColor(0, 0, 0, 220), IsAntialias = true };
            using var bgPaint = new SKPaint { Color = new SKColor(255, 255, 200, 230) };
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
            using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawRect(skRect, fillPaint);
        }
        using var strokePaint = new SKPaint
        {
            Color = color.WithAlpha(255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = rect.StrokeWidth,
            IsAntialias = true,
        };
        canvas.DrawRect(skRect, strokePaint);
    }

    private static void DrawSelectionIndicator(SKCanvas canvas, Annotation annotation)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return;

        using var dashPaint = new SKPaint
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
                float minX = h.Rects.Min(r => r.X);
                float minY = h.Rects.Min(r => r.Y);
                float maxX = h.Rects.Max(r => r.X + r.W);
                float maxY = h.Rects.Max(r => r.Y + r.H);
                return new SKRect(minX, minY, maxX, maxY);
            case FreehandAnnotation f when f.Points.Count > 0:
                float fMinX = f.Points.Min(p => p.X);
                float fMinY = f.Points.Min(p => p.Y);
                float fMaxX = f.Points.Max(p => p.X);
                float fMaxY = f.Points.Max(p => p.Y);
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
