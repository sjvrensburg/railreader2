using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;

namespace RailReader2.Controls;

/// <summary>
/// A simple radial menu with wedge segments around a centre button.
/// Each segment has a label, an icon (Font Awesome Unicode char), and an action.
/// To add new segments, simply append to the list passed to SetSegments().
/// Use IconChars constants for common icons, or any Font Awesome solid codepoint.
/// </summary>
public class RadialMenu : Control
{
    /// <summary>
    /// Font Awesome Solid icon codepoints.
    /// Full reference: https://fontawesome.com/v6/icons?s=solid
    /// </summary>
    public static class IconChars
    {
        public const string Highlighter = "\uf591";
        public const string Pen         = "\uf304";
        public const string TextHeight  = "\uf034";
        public const string Square      = "\uf0c8";
        public const string Eraser      = "\uf12d";
        public const string Xmark       = "\uf00d";
        public const string PaintBrush  = "\uf1fc";
        public const string Bookmark    = "\uf02e";
        public const string Shapes      = "\uf61f";
        public const string Comment     = "\uf075";
        public const string CirclePlus  = "\uf055";
        public const string Palette     = "\uf53f";
        public const string ICursor    = "\uf246";
        public const string Copy       = "\uf0c5";
    }

    public record Segment(string Label, string Icon, Action Action);

    private static SKTypeface? s_iconTypeface;
    private readonly List<Segment> _segments = [];
    private int _hoveredIndex = -1;
    private bool _hoveringCentre;
    private Action? _onClose;

    private double _scale = 1.0;

    public double InnerRadius { get; set; } = 30;
    public double OuterRadius { get; set; } = 95;

    /// <summary>
    /// UI scale factor (typically AppConfig.UiFontScale). Scales radii, font sizes, and control dimensions.
    /// </summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (Math.Abs(_scale - value) < 0.001) return;
            _scale = value;
            ApplyScale();
        }
    }

    public RadialMenu()
    {
        IsHitTestVisible = true;
        ApplyScale();
        EnsureIconFont();
    }

    private void ApplyScale()
    {
        double s = _scale;
        InnerRadius = 30 * s;
        OuterRadius = 95 * s;
        Width = 210 * s;
        Height = 210 * s;
        InvalidateVisual();
    }

    private static void EnsureIconFont()
    {
        if (s_iconTypeface is not null) return;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("fa-solid-900.ttf", StringComparison.OrdinalIgnoreCase));
            if (resourceName is not null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is not null)
                    s_iconTypeface = SKTypeface.FromStream(stream);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RadialMenu] Failed to load icon font: {ex.Message}");
        }
    }

    public void SetSegments(List<Segment> segments, Action onClose)
    {
        _segments.Clear();
        _segments.AddRange(segments);
        _onClose = onClose;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        double cx = Width / 2, cy = Height / 2;
        double dx = pos.X - cx, dy = pos.Y - cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        int oldHover = _hoveredIndex;
        bool oldCentre = _hoveringCentre;

        if (dist < InnerRadius)
        {
            _hoveringCentre = true;
            _hoveredIndex = -1;
        }
        else if (dist < OuterRadius && _segments.Count > 0)
        {
            _hoveringCentre = false;
            double angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += 2 * Math.PI;
            double segAngle = 2 * Math.PI / _segments.Count;
            double offset = -Math.PI / 2 - segAngle / 2;
            double adjusted = angle - offset;
            if (adjusted < 0) adjusted += 2 * Math.PI;
            _hoveredIndex = (int)(adjusted / segAngle) % _segments.Count;
        }
        else
        {
            _hoveringCentre = false;
            _hoveredIndex = -1;
        }

        if (_hoveredIndex != oldHover || _hoveringCentre != oldCentre)
            InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_hoveringCentre)
            _onClose?.Invoke();
        else if (_hoveredIndex >= 0 && _hoveredIndex < _segments.Count)
            _segments[_hoveredIndex].Action.Invoke();
        else
            _onClose?.Invoke();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double w = Width > 0 ? Width : Bounds.Width;
        double h = Height > 0 ? Height : Bounds.Height;
        context.Custom(new RadialMenuDrawOp(
            new Rect(0, 0, w, h), _segments, InnerRadius, OuterRadius,
            _hoveredIndex, _hoveringCentre, s_iconTypeface, _scale));
    }

    private sealed class RadialMenuDrawOp : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly List<Segment> _segments;
        private readonly double _innerR, _outerR;
        private readonly int _hovered;
        private readonly bool _hoverCentre;
        private readonly SKTypeface? _iconTypeface;
        private readonly float _scale;

        public RadialMenuDrawOp(Rect bounds, List<Segment> segments,
            double innerR, double outerR, int hovered, bool hoverCentre,
            SKTypeface? iconTypeface, double scale)
        {
            _bounds = bounds;
            _segments = [.. segments];
            _innerR = innerR;
            _outerR = outerR;
            _hovered = hovered;
            _hoverCentre = hoverCentre;
            _iconTypeface = iconTypeface;
            _scale = (float)scale;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => true;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            float cx = (float)_bounds.Width / 2;
            float cy = (float)_bounds.Height / 2;
            float innerR = (float)_innerR;
            float outerR = (float)_outerR;

            // Drop shadow
            using var shadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 100),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * _scale),
            };
            canvas.DrawCircle(cx, cy + 2 * _scale, outerR + 2 * _scale, shadowPaint);

            // Background circle
            using var bgPaint = new SKPaint { Color = new SKColor(38, 38, 42, 245), IsAntialias = true };
            canvas.DrawCircle(cx, cy, outerR, bgPaint);

            // Outer ring border
            using var ringPaint = new SKPaint
            {
                Color = new SKColor(70, 70, 74, 200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true,
            };
            canvas.DrawCircle(cx, cy, outerR, ringPaint);

            if (_segments.Count > 0)
            {
                float segAngle = 360f / _segments.Count;
                float startOffset = -90f - segAngle / 2;

                for (int i = 0; i < _segments.Count; i++)
                {
                    float startA = startOffset + i * segAngle;
                    bool hovered = i == _hovered;

                    // Wedge fill
                    var wedgeColor = hovered
                        ? new SKColor(0, 120, 212, 210)
                        : new SKColor(52, 52, 56, 220);
                    using var wedgePaint = new SKPaint { Color = wedgeColor, IsAntialias = true };

                    using var wedgePath = new SKPath();
                    var outerRect = new SKRect(cx - outerR, cy - outerR, cx + outerR, cy + outerR);
                    var innerRect = new SKRect(cx - innerR, cy - innerR, cx + innerR, cy + innerR);
                    wedgePath.ArcTo(outerRect, startA, segAngle, true);
                    wedgePath.ArcTo(innerRect, startA + segAngle, -segAngle, false);
                    wedgePath.Close();
                    canvas.DrawPath(wedgePath, wedgePaint);

                    // Divider line
                    double rad = startA * Math.PI / 180;
                    using var divPaint = new SKPaint
                    {
                        Color = new SKColor(70, 70, 74, 180),
                        StrokeWidth = 1,
                        IsAntialias = true,
                    };
                    canvas.DrawLine(
                        cx + innerR * (float)Math.Cos(rad), cy + innerR * (float)Math.Sin(rad),
                        cx + outerR * (float)Math.Cos(rad), cy + outerR * (float)Math.Sin(rad),
                        divPaint);

                    // Icon at segment midpoint
                    double midAngle = (startA + segAngle / 2) * Math.PI / 180;
                    float midR = (innerR + outerR) / 2;
                    float tx = cx + midR * (float)Math.Cos(midAngle);
                    float ty = cy + midR * (float)Math.Sin(midAngle);

                    var iconColor = hovered ? SKColors.White : new SKColor(210, 210, 214);
                    DrawIcon(canvas, _segments[i].Icon, tx, ty, 18f * _scale, iconColor);

                    // Label below icon on hover
                    if (hovered)
                    {
                        float labelSize = 11f * _scale;
                        using var labelFont = new SKFont(SKTypeface.Default, labelSize);
                        using var labelPaint = new SKPaint { Color = new SKColor(255, 255, 255, 220), IsAntialias = true };
                        float labelW = labelFont.MeasureText(_segments[i].Label);
                        canvas.DrawText(_segments[i].Label, tx - labelW / 2, ty + 16 * _scale, labelFont, labelPaint);
                    }
                }
            }

            // Inner ring border
            canvas.DrawCircle(cx, cy, innerR, ringPaint);

            // Centre button
            var centreColor = _hoverCentre
                ? new SKColor(200, 50, 50, 240)
                : new SKColor(48, 48, 52, 245);
            using var centrePaint = new SKPaint { Color = centreColor, IsAntialias = true };
            canvas.DrawCircle(cx, cy, innerR, centrePaint);

            // Centre X icon
            var xColor = _hoverCentre ? SKColors.White : new SKColor(170, 170, 174);
            DrawIcon(canvas, IconChars.Xmark, cx, cy, 16f * _scale, xColor);
        }

        private void DrawIcon(SKCanvas canvas, string iconChar, float x, float y, float size, SKColor color)
        {
            var typeface = _iconTypeface ?? SKTypeface.Default;
            using var font = new SKFont(typeface, size);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            float w = font.MeasureText(iconChar);
            // Centre vertically: offset by ~40% of font size (ascent approximation)
            canvas.DrawText(iconChar, x - w / 2, y + size * 0.38f, font, paint);
        }
    }
}
