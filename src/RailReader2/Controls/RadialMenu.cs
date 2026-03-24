using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader.Renderer.Skia;

namespace RailReader2.Controls;

/// <summary>
/// A simple radial menu with wedge segments around a centre button.
/// Each segment has a label, an icon (Font Awesome Unicode char), and an action.
/// To add new segments, simply append to the list passed to SetSegments().
/// Use IconChars constants for common icons, or any Font Awesome solid codepoint.
/// When a segment has ColorOptions, tapping it shows an outer arc of colour dots
/// instead of immediately activating the tool.
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
    }

    public record ColorOption(string HexColor, float Opacity, Action SelectAction);

    public record Segment(string Label, string Icon, Action Action,
        List<ColorOption>? ColorOptions = null, int ActiveColorIndex = 0);

    private static SKTypeface? s_iconTypeface;
    private readonly List<Segment> _segments = [];
    private int _hoveredIndex = -1;
    private bool _hoveringCentre;
    private Action? _onClose;

    // Outer colour ring state
    private int _expandedSegment = -1;
    private int _hoveredColorIndex = -1;

    private double _scale = 1.0;

    public double InnerRadius { get; set; } = 30;
    public double OuterRadius { get; set; } = 95;
    private double ColorRingRadius => OuterRadius + 22 * _scale;

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
        Width = 260 * s;  // Wider to accommodate colour ring
        Height = 260 * s;
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
        _expandedSegment = -1;
        _hoveredColorIndex = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Update the active colour index for a segment (e.g. after controller state changes).
    /// </summary>
    public void UpdateSegmentColorIndex(int segmentIndex, int colorIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= _segments.Count) return;
        var seg = _segments[segmentIndex];
        _segments[segmentIndex] = seg with { ActiveColorIndex = colorIndex };
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
        int oldColorHover = _hoveredColorIndex;

        _hoveredColorIndex = -1;

        if (dist < InnerRadius)
        {
            _hoveringCentre = true;
            _hoveredIndex = -1;
        }
        else if (_expandedSegment >= 0 && dist >= OuterRadius && dist <= ColorRingRadius + 10 * _scale)
        {
            // Check if hovering a colour dot in the expanded segment
            _hoveringCentre = false;
            _hoveredIndex = _expandedSegment;
            var seg = _segments[_expandedSegment];
            if (seg.ColorOptions is { Count: > 0 } opts)
            {
                int hitIdx = HitTestColorDot(dx, dy, _expandedSegment, opts.Count);
                _hoveredColorIndex = hitIdx;
            }
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

        if (_hoveredIndex != oldHover || _hoveringCentre != oldCentre || _hoveredColorIndex != oldColorHover)
            InvalidateVisual();
    }

    private int HitTestColorDot(double dx, double dy, int segIndex, int colorCount)
    {
        double segAngle = 2 * Math.PI / _segments.Count;
        double startAngle = -Math.PI / 2 - segAngle / 2 + segIndex * segAngle;
        double dotR = (OuterRadius + ColorRingRadius) / 2;
        double dotSize = 7 * _scale;

        for (int i = 0; i < colorCount; i++)
        {
            double t = (i + 0.5) / colorCount;
            double angle = startAngle + t * segAngle;
            double dotX = dotR * Math.Cos(angle);
            double dotY = dotR * Math.Sin(angle);
            double ddx = dx - dotX, ddy = dy - dotY;
            if (ddx * ddx + ddy * ddy <= (dotSize + 4 * _scale) * (dotSize + 4 * _scale))
                return i;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Check colour dot tap first
        if (_expandedSegment >= 0 && _hoveredColorIndex >= 0)
        {
            var seg = _segments[_expandedSegment];
            if (seg.ColorOptions is { } opts && _hoveredColorIndex < opts.Count)
            {
                opts[_hoveredColorIndex].SelectAction.Invoke();
                _expandedSegment = -1;
                _hoveredColorIndex = -1;
            }
            e.Handled = true;
            return;
        }

        if (_hoveredIndex >= 0 && _hoveredIndex < _segments.Count)
        {
            var seg = _segments[_hoveredIndex];
            if (seg.ColorOptions is { Count: > 0 })
            {
                // Toggle expanded state for this segment
                _expandedSegment = _expandedSegment == _hoveredIndex ? -1 : _hoveredIndex;
                InvalidateVisual();
            }
            else
            {
                _expandedSegment = -1;
                seg.Action.Invoke();
            }
        }
        else if (_expandedSegment >= 0)
        {
            // Clicking outside while colour ring is expanded:
            // activate the tool with the last-used colour, then close.
            _segments[_expandedSegment].Action.Invoke();
            _expandedSegment = -1;
            _onClose?.Invoke();
        }
        else
        {
            _onClose?.Invoke();
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double w = Width > 0 ? Width : Bounds.Width;
        double h = Height > 0 ? Height : Bounds.Height;
        context.Custom(new RadialMenuDrawOp(
            new Rect(0, 0, w, h), _segments, InnerRadius, OuterRadius,
            _hoveredIndex, _hoveringCentre, s_iconTypeface, _scale,
            _expandedSegment, _hoveredColorIndex, ColorRingRadius));
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
        private readonly int _expandedSegment;
        private readonly int _hoveredColorIndex;
        private readonly double _colorRingR;

        public RadialMenuDrawOp(Rect bounds, List<Segment> segments,
            double innerR, double outerR, int hovered, bool hoverCentre,
            SKTypeface? iconTypeface, double scale,
            int expandedSegment, int hoveredColorIndex, double colorRingR)
        {
            _bounds = bounds;
            _segments = [.. segments];
            _innerR = innerR;
            _outerR = outerR;
            _hovered = hovered;
            _hoverCentre = hoverCentre;
            _iconTypeface = iconTypeface;
            _scale = (float)scale;
            _expandedSegment = expandedSegment;
            _hoveredColorIndex = hoveredColorIndex;
            _colorRingR = colorRingR;
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
                    bool expanded = i == _expandedSegment;

                    // Wedge fill
                    SKColor wedgeColor;
                    if (expanded)
                        wedgeColor = new SKColor(0, 100, 180, 210);
                    else if (hovered)
                        wedgeColor = new SKColor(0, 120, 212, 210);
                    else
                        wedgeColor = new SKColor(52, 52, 56, 220);
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

                    var iconColor = hovered || expanded ? SKColors.White : new SKColor(210, 210, 214);
                    DrawIcon(canvas, _segments[i].Icon, tx, ty, 18f * _scale, iconColor);

                    // Active colour indicator dot on segment
                    if (_segments[i].ColorOptions is { Count: > 0 } opts)
                    {
                        int activeIdx = _segments[i].ActiveColorIndex;
                        if (activeIdx >= 0 && activeIdx < opts.Count)
                        {
                            var ac = opts[activeIdx];
                            var dotColor = ParseHexColor(ac.HexColor, (byte)(ac.Opacity * 255));
                            float indicatorR = 4f * _scale;
                            float indicatorDist = midR + 16 * _scale;
                            float ix = cx + indicatorDist * (float)Math.Cos(midAngle);
                            float iy = cy + indicatorDist * (float)Math.Sin(midAngle);
                            using var dotPaint = new SKPaint { Color = dotColor, IsAntialias = true };
                            canvas.DrawCircle(ix, iy, indicatorR, dotPaint);
                            using var dotBorder = new SKPaint
                            {
                                Color = SKColors.White,
                                Style = SKPaintStyle.Stroke,
                                StrokeWidth = 1,
                                IsAntialias = true,
                            };
                            canvas.DrawCircle(ix, iy, indicatorR, dotBorder);
                        }
                    }

                    // Label below icon on hover
                    if (hovered && !expanded)
                    {
                        float labelSize = 11f * _scale;
                        using var labelFont = new SKFont(SKTypeface.Default, labelSize);
                        using var labelPaint = new SKPaint { Color = new SKColor(255, 255, 255, 220), IsAntialias = true };
                        float labelW = labelFont.MeasureText(_segments[i].Label);
                        canvas.DrawText(_segments[i].Label, tx - labelW / 2, ty + 16 * _scale, labelFont, labelPaint);
                    }
                }

                // Draw colour ring for expanded segment
                if (_expandedSegment >= 0 && _expandedSegment < _segments.Count)
                {
                    var seg = _segments[_expandedSegment];
                    if (seg.ColorOptions is { Count: > 0 } colorOpts)
                    {
                        float segAngleRad = (float)(2 * Math.PI / _segments.Count);
                        float startAngleRad = (float)(-Math.PI / 2 - segAngleRad / 2 + _expandedSegment * segAngleRad);
                        float dotR = (float)((_outerR + _colorRingR) / 2);
                        float dotSize = 7f * _scale;

                        for (int ci = 0; ci < colorOpts.Count; ci++)
                        {
                            float t = (ci + 0.5f) / colorOpts.Count;
                            float angle = startAngleRad + t * segAngleRad;
                            float dotX = cx + dotR * (float)Math.Cos(angle);
                            float dotY = cy + dotR * (float)Math.Sin(angle);

                            var opt = colorOpts[ci];
                            var fillColor = ParseHexColor(opt.HexColor, (byte)(opt.Opacity * 255));
                            bool isHovered = ci == _hoveredColorIndex;

                            // Background circle for visibility
                            using var bgDot = new SKPaint
                            {
                                Color = new SKColor(38, 38, 42, 240),
                                IsAntialias = true,
                            };
                            canvas.DrawCircle(dotX, dotY, dotSize + 2 * _scale, bgDot);

                            // Colour fill
                            using var colorPaint = new SKPaint { Color = fillColor, IsAntialias = true };
                            canvas.DrawCircle(dotX, dotY, dotSize, colorPaint);

                            // Full-opacity border for visibility
                            var borderColor = ParseHexColor(opt.HexColor, 255);
                            using var border = new SKPaint
                            {
                                Color = borderColor,
                                Style = SKPaintStyle.Stroke,
                                StrokeWidth = 1.5f,
                                IsAntialias = true,
                            };
                            canvas.DrawCircle(dotX, dotY, dotSize, border);

                            // Hover highlight
                            if (isHovered)
                            {
                                using var hoverRing = new SKPaint
                                {
                                    Color = SKColors.White,
                                    Style = SKPaintStyle.Stroke,
                                    StrokeWidth = 2f,
                                    IsAntialias = true,
                                };
                                canvas.DrawCircle(dotX, dotY, dotSize + 2 * _scale, hoverRing);
                            }

                            // Active indicator (small checkmark-like inner ring)
                            if (ci == seg.ActiveColorIndex)
                            {
                                using var activePaint = new SKPaint
                                {
                                    Color = SKColors.White,
                                    Style = SKPaintStyle.Stroke,
                                    StrokeWidth = 2f,
                                    IsAntialias = true,
                                };
                                canvas.DrawCircle(dotX, dotY, dotSize - 3 * _scale, activePaint);
                            }
                        }
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

        private static SKColor ParseHexColor(string hex, byte alpha)
            => RailReader.Core.Services.ColorUtils.ParseHexColor(hex, alpha).ToSKColor();
    }
}
