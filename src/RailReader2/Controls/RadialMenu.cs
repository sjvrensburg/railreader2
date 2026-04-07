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
///
/// Three rings:
///   Inner  — tool wedges (always visible)
///   Middle — thickness dots (size-varied circles), shown when segment has ThicknessOptions
///   Outer  — colour dots, shown when segment has ColorOptions
/// Tapping a segment with options expands the relevant ring(s).
/// </summary>
public class RadialMenu : Control
{
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
    public record ThicknessOption(float StrokeWidth, Action SelectAction);

    public record Segment(string Label, string Icon, Action Action,
        List<ColorOption>? ColorOptions = null, int ActiveColorIndex = 0,
        List<ThicknessOption>? ThicknessOptions = null, int ActiveThicknessIndex = 0);

    private static SKTypeface? s_iconTypeface;
    private readonly List<Segment> _segments = [];
    private int _hoveredIndex = -1;
    private bool _hoveringCentre;
    private Action? _onClose;

    // Expanded ring state
    private int _expandedSegment = -1;
    private int _hoveredColorIndex = -1;
    private int _hoveredThicknessIndex = -1;

    private double _scale = 1.0;

    public double InnerRadius { get; set; } = 30;
    public double OuterRadius { get; set; } = 95;
    private double ThicknessRingRadius => OuterRadius + 28 * _scale;
    private double ColorRingRadius => OuterRadius + 64 * _scale;

    /// <summary>
    /// Returns the correct colour ring radius for a segment — closer ring when
    /// the segment has no thickness options (only colour), further out when both exist.
    /// </summary>
    private double EffectiveColorRingRadius(int segIndex)
    {
        if (segIndex < 0 || segIndex >= _segments.Count) return ColorRingRadius;
        return _segments[segIndex].ThicknessOptions is { Count: > 0 }
            ? ColorRingRadius
            : ThicknessRingRadius; // use closer ring when thickness ring is absent
    }

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
        Width = 360 * s;  // wider to accommodate two outer rings
        Height = 360 * s;
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
        _hoveredThicknessIndex = -1;
        InvalidateVisual();
    }

    public void UpdateSegmentColorIndex(int segmentIndex, int colorIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= _segments.Count) return;
        var seg = _segments[segmentIndex];
        _segments[segmentIndex] = seg with { ActiveColorIndex = colorIndex };
        InvalidateVisual();
    }

    public void UpdateSegmentThicknessIndex(int segmentIndex, int thicknessIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= _segments.Count) return;
        var seg = _segments[segmentIndex];
        _segments[segmentIndex] = seg with { ActiveThicknessIndex = thicknessIndex };
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
        int oldThicknessHover = _hoveredThicknessIndex;

        _hoveredColorIndex = -1;
        _hoveredThicknessIndex = -1;

        if (dist < InnerRadius)
        {
            _hoveringCentre = true;
            _hoveredIndex = -1;
        }
        else if (_expandedSegment >= 0 && dist >= OuterRadius)
        {
            // Check colour and thickness rings
            _hoveringCentre = false;
            _hoveredIndex = _expandedSegment;
            var seg = _segments[_expandedSegment];

            bool hasThickness = seg.ThicknessOptions is { Count: > 0 };

            // Try colour ring first (outermost)
            if (seg.ColorOptions is { Count: > 0 } cOpts)
            {
                double cRingR = EffectiveColorRingRadius(_expandedSegment);
                double cInner = hasThickness ? ThicknessRingRadius : OuterRadius;
                int hitC = HitTestRingDot(dx, dy, _expandedSegment, cOpts.Count, cRingR, cInner);
                if (hitC >= 0) _hoveredColorIndex = hitC;
            }

            // Try thickness ring (middle)
            if (_hoveredColorIndex < 0 && hasThickness)
            {
                var tOpts = seg.ThicknessOptions!;
                int hitT = HitTestRingDot(dx, dy, _expandedSegment, tOpts.Count,
                    ThicknessRingRadius, OuterRadius);
                if (hitT >= 0) _hoveredThicknessIndex = hitT;
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

        if (_hoveredIndex != oldHover || _hoveringCentre != oldCentre
            || _hoveredColorIndex != oldColorHover || _hoveredThicknessIndex != oldThicknessHover)
            InvalidateVisual();
    }

    private int HitTestRingDot(double dx, double dy, int segIndex, int dotCount,
        double ringRadius, double innerEdge)
    {
        double segAngle = 2 * Math.PI / _segments.Count;
        double startAngle = -Math.PI / 2 - segAngle / 2 + segIndex * segAngle;
        double dotR = (innerEdge + ringRadius) / 2;
        double hitSize = 9 * _scale;

        for (int i = 0; i < dotCount; i++)
        {
            double t = (i + 0.5) / dotCount;
            double angle = startAngle + t * segAngle;
            double dotX = dotR * Math.Cos(angle);
            double dotY = dotR * Math.Sin(angle);
            double ddx = dx - dotX, ddy = dy - dotY;
            if (ddx * ddx + ddy * ddy <= hitSize * hitSize)
                return i;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Check colour dot tap — closes menu
        if (_expandedSegment >= 0 && _hoveredColorIndex >= 0)
        {
            var seg = _segments[_expandedSegment];
            if (seg.ColorOptions is { } opts && _hoveredColorIndex < opts.Count)
            {
                opts[_hoveredColorIndex].SelectAction.Invoke();
                _expandedSegment = -1;
                _hoveredColorIndex = -1;
                _onClose?.Invoke();
            }
            e.Handled = true;
            return;
        }

        // Check thickness dot tap — stays open for colour selection
        if (_expandedSegment >= 0 && _hoveredThicknessIndex >= 0)
        {
            var seg = _segments[_expandedSegment];
            if (seg.ThicknessOptions is { } opts && _hoveredThicknessIndex < opts.Count)
            {
                opts[_hoveredThicknessIndex].SelectAction.Invoke();
                _hoveredThicknessIndex = -1;
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (_hoveredIndex >= 0 && _hoveredIndex < _segments.Count)
        {
            var seg = _segments[_hoveredIndex];
            bool hasOptions = seg.ColorOptions is { Count: > 0 } || seg.ThicknessOptions is { Count: > 0 };
            if (hasOptions)
            {
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
            // Clicking outside while ring is expanded: activate tool with current settings, close
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
        double effectiveColorR = _expandedSegment >= 0
            ? EffectiveColorRingRadius(_expandedSegment) : ColorRingRadius;
        context.Custom(new RadialMenuDrawOp(
            new Rect(0, 0, w, h), _segments, InnerRadius, OuterRadius,
            _hoveredIndex, _hoveringCentre, s_iconTypeface, _scale,
            _expandedSegment, _hoveredColorIndex, _hoveredThicknessIndex,
            ThicknessRingRadius, effectiveColorR));
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
        private readonly int _hoveredThicknessIndex;
        private readonly double _thicknessRingR;
        private readonly double _colorRingR;

        // Cached paints — reused across frames
        [ThreadStatic] private static float s_cachedShadowScale;
        [ThreadStatic] private static SKPaint? s_shadowPaint;
        [ThreadStatic] private static SKPaint? s_bgPaint;
        [ThreadStatic] private static SKPaint? s_ringPaint;
        [ThreadStatic] private static SKPaint? s_divPaint;
        [ThreadStatic] private static SKPaint? s_fillPaint;
        [ThreadStatic] private static SKPaint? s_strokePaint;
        [ThreadStatic] private static SKPaint? s_labelPaint;
        [ThreadStatic] private static SKFont? s_labelFont;
        [ThreadStatic] private static SKPaint? s_iconPaint;
        [ThreadStatic] private static SKFont? s_iconFont;

        public RadialMenuDrawOp(Rect bounds, List<Segment> segments,
            double innerR, double outerR, int hovered, bool hoverCentre,
            SKTypeface? iconTypeface, double scale,
            int expandedSegment, int hoveredColorIndex, int hoveredThicknessIndex,
            double thicknessRingR, double colorRingR)
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
            _hoveredThicknessIndex = hoveredThicknessIndex;
            _thicknessRingR = thicknessRingR;
            _colorRingR = colorRingR;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is RadialMenuDrawOp op
            && _bounds == op._bounds
            && _hovered == op._hovered
            && _hoverCentre == op._hoverCentre
            && _expandedSegment == op._expandedSegment
            && _hoveredColorIndex == op._hoveredColorIndex
            && _hoveredThicknessIndex == op._hoveredThicknessIndex
            && _colorRingR == op._colorRingR
            && _segments.Count == op._segments.Count;
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

            // Cached paints
            var shadowPaint = s_shadowPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 100), IsAntialias = true };
            if (shadowPaint.MaskFilter is null || s_cachedShadowScale != _scale)
            {
                shadowPaint.MaskFilter?.Dispose();
                shadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * _scale);
                s_cachedShadowScale = _scale;
            }

            var bgPaint = s_bgPaint ??= new SKPaint { Color = new SKColor(38, 38, 42, 245), IsAntialias = true };

            var ringPaint = s_ringPaint ??= new SKPaint
            {
                Color = new SKColor(70, 70, 74, 200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true,
            };

            var divPaint = s_divPaint ??= new SKPaint
            {
                Color = new SKColor(70, 70, 74, 180),
                StrokeWidth = 1,
                IsAntialias = true,
            };

            var fillPaint = s_fillPaint ??= new SKPaint { IsAntialias = true };
            var strokePaint = s_strokePaint ??= new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
            };

            var labelPaint = s_labelPaint ??= new SKPaint { Color = new SKColor(255, 255, 255, 220), IsAntialias = true };
            var labelFont = s_labelFont ??= new SKFont(SKTypeface.Default, 11f);

            // Drop shadow
            canvas.DrawCircle(cx, cy + 2 * _scale, outerR + 2 * _scale, shadowPaint);

            // Background circle
            canvas.DrawCircle(cx, cy, outerR, bgPaint);

            // Outer ring border
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
                    if (expanded)
                        fillPaint.Color = new SKColor(0, 100, 180, 210);
                    else if (hovered)
                        fillPaint.Color = new SKColor(0, 120, 212, 210);
                    else
                        fillPaint.Color = new SKColor(52, 52, 56, 220);

                    using var wedgePath = new SKPath();
                    var outerRect = new SKRect(cx - outerR, cy - outerR, cx + outerR, cy + outerR);
                    var innerRect = new SKRect(cx - innerR, cy - innerR, cx + innerR, cy + innerR);
                    wedgePath.ArcTo(outerRect, startA, segAngle, true);
                    wedgePath.ArcTo(innerRect, startA + segAngle, -segAngle, false);
                    wedgePath.Close();
                    canvas.DrawPath(wedgePath, fillPaint);

                    // Divider line
                    double rad = startA * Math.PI / 180;
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

                    // Active colour indicator dot on segment edge
                    if (_segments[i].ColorOptions is { Count: > 0 } opts)
                    {
                        int activeIdx = _segments[i].ActiveColorIndex;
                        if (activeIdx >= 0 && activeIdx < opts.Count)
                        {
                            var ac = opts[activeIdx];
                            float indicatorR = 4f * _scale;
                            float indicatorDist = midR + 16 * _scale;
                            float ix = cx + indicatorDist * (float)Math.Cos(midAngle);
                            float iy = cy + indicatorDist * (float)Math.Sin(midAngle);
                            fillPaint.Color = ParseHexColor(ac.HexColor, (byte)(ac.Opacity * 255));
                            canvas.DrawCircle(ix, iy, indicatorR, fillPaint);
                            strokePaint.Color = SKColors.White;
                            strokePaint.StrokeWidth = 1;
                            canvas.DrawCircle(ix, iy, indicatorR, strokePaint);
                        }
                    }

                    // Label below icon on hover
                    if (hovered && !expanded)
                    {
                        labelFont.Size = 11f * _scale;
                        float labelW = labelFont.MeasureText(_segments[i].Label);
                        canvas.DrawText(_segments[i].Label, tx - labelW / 2, ty + 16 * _scale, labelFont, labelPaint);
                    }
                }

                // Draw expanded rings for selected segment
                if (_expandedSegment >= 0 && _expandedSegment < _segments.Count)
                {
                    var seg = _segments[_expandedSegment];

                    // Thickness ring (middle)
                    if (seg.ThicknessOptions is { Count: > 0 } thicknessOpts)
                    {
                        DrawThicknessRing(canvas, cx, cy, _expandedSegment, thicknessOpts,
                            seg.ActiveThicknessIndex, fillPaint, strokePaint);
                    }

                    // Colour ring (outer, or middle if no thickness)
                    if (seg.ColorOptions is { Count: > 0 } colorOpts)
                    {
                        DrawColorRing(canvas, cx, cy, _expandedSegment, colorOpts,
                            seg.ActiveColorIndex, _colorRingR, fillPaint, strokePaint);
                    }
                }
            }

            // Inner ring border
            canvas.DrawCircle(cx, cy, innerR, ringPaint);

            // Centre button
            fillPaint.Color = _hoverCentre
                ? new SKColor(200, 50, 50, 240)
                : new SKColor(48, 48, 52, 245);
            canvas.DrawCircle(cx, cy, innerR, fillPaint);

            // Centre X icon
            var xColor = _hoverCentre ? SKColors.White : new SKColor(170, 170, 174);
            DrawIcon(canvas, IconChars.Xmark, cx, cy, 16f * _scale, xColor);
        }

        /// <summary>
        /// Draws a filled arc band (wedge between two radii) behind a ring's dots for legibility.
        /// </summary>
        private void DrawArcBackground(SKCanvas canvas, float cx, float cy, int segIndex,
            double ringRadius, SKPaint fillPaint)
            => DrawArcBackground(canvas, cx, cy, segIndex, ringRadius, _outerR, fillPaint);

        private void DrawArcBackground(SKCanvas canvas, float cx, float cy, int segIndex,
            double ringRadius, double innerEdge, SKPaint fillPaint)
        {
            float segAngleDeg = 360f / _segments.Count;
            float startA = -90f - segAngleDeg / 2 + segIndex * segAngleDeg;
            float bandInner = (float)innerEdge + 2 * _scale;
            float bandOuter = (float)ringRadius + 2 * _scale;

            using var path = new SKPath();
            var outerRect = new SKRect(cx - bandOuter, cy - bandOuter, cx + bandOuter, cy + bandOuter);
            var innerRect = new SKRect(cx - bandInner, cy - bandInner, cx + bandInner, cy + bandInner);
            path.ArcTo(outerRect, startA, segAngleDeg, true);
            path.ArcTo(innerRect, startA + segAngleDeg, -segAngleDeg, false);
            path.Close();

            fillPaint.Color = new SKColor(38, 38, 42, 235);
            canvas.DrawPath(path, fillPaint);
        }

        private void DrawThicknessRing(SKCanvas canvas, float cx, float cy, int segIndex,
            List<ThicknessOption> opts, int activeIdx, SKPaint fillPaint, SKPaint strokePaint)
        {
            float segAngleRad = (float)(2 * Math.PI / _segments.Count);
            float startAngleRad = (float)(-Math.PI / 2 - segAngleRad / 2 + segIndex * segAngleRad);
            float dotR = (float)((_outerR + _thicknessRingR) / 2);

            DrawArcBackground(canvas, cx, cy, segIndex, _thicknessRingR, fillPaint);

            for (int i = 0; i < opts.Count; i++)
            {
                float t = (i + 0.5f) / opts.Count;
                float angle = startAngleRad + t * segAngleRad;
                float dotX = cx + dotR * (float)Math.Cos(angle);
                float dotY = cy + dotR * (float)Math.Sin(angle);

                bool isHovered = i == _hoveredThicknessIndex;

                // Size-varied circle: radius proportional to stroke width
                float strokeW = opts[i].StrokeWidth;
                float circleR = (2f + strokeW * 1.2f) * _scale;

                // Filled circle representing thickness
                fillPaint.Color = new SKColor(200, 200, 204);
                canvas.DrawCircle(dotX, dotY, circleR, fillPaint);

                // Hover highlight
                if (isHovered)
                {
                    strokePaint.Color = SKColors.White;
                    strokePaint.StrokeWidth = 2f;
                    canvas.DrawCircle(dotX, dotY, circleR + 2 * _scale, strokePaint);
                }

                // Active indicator
                if (i == activeIdx)
                {
                    strokePaint.Color = new SKColor(0, 120, 212);
                    strokePaint.StrokeWidth = 2f;
                    canvas.DrawCircle(dotX, dotY, circleR + 2 * _scale, strokePaint);
                }
            }
        }

        private void DrawColorRing(SKCanvas canvas, float cx, float cy, int segIndex,
            List<ColorOption> opts, int activeIdx, double ringRadius,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            float segAngleRad = (float)(2 * Math.PI / _segments.Count);
            float startAngleRad = (float)(-Math.PI / 2 - segAngleRad / 2 + segIndex * segAngleRad);
            float dotSize = 7f * _scale;

            // Arc background behind colour dots — use the band from the inner edge of
            // this ring to its outer edge. When a thickness ring exists below, the arc
            // starts from the thickness ring's outer edge to avoid overlap.
            bool hasThicknessRing = _segments[segIndex].ThicknessOptions is { Count: > 0 };
            double arcInner = hasThicknessRing ? _thicknessRingR : _outerR;
            // Position dots at midpoint of the actual band (not from outerR)
            float dotR = (float)((arcInner + ringRadius) / 2);
            DrawArcBackground(canvas, cx, cy, segIndex, ringRadius, arcInner, fillPaint);

            for (int ci = 0; ci < opts.Count; ci++)
            {
                float t = (ci + 0.5f) / opts.Count;
                float angle = startAngleRad + t * segAngleRad;
                float dotX = cx + dotR * (float)Math.Cos(angle);
                float dotY = cy + dotR * (float)Math.Sin(angle);

                var opt = opts[ci];
                bool isHovered = ci == _hoveredColorIndex;

                // Background circle for visibility
                fillPaint.Color = new SKColor(38, 38, 42, 240);
                canvas.DrawCircle(dotX, dotY, dotSize + 2 * _scale, fillPaint);

                // Colour fill
                fillPaint.Color = ParseHexColor(opt.HexColor, (byte)(opt.Opacity * 255));
                canvas.DrawCircle(dotX, dotY, dotSize, fillPaint);

                // Full-opacity border for visibility
                strokePaint.Color = ParseHexColor(opt.HexColor, 255);
                strokePaint.StrokeWidth = 1.5f;
                canvas.DrawCircle(dotX, dotY, dotSize, strokePaint);

                // Hover highlight
                if (isHovered)
                {
                    strokePaint.Color = SKColors.White;
                    strokePaint.StrokeWidth = 2f;
                    canvas.DrawCircle(dotX, dotY, dotSize + 2 * _scale, strokePaint);
                }

                // Active indicator
                if (ci == activeIdx)
                {
                    strokePaint.Color = SKColors.White;
                    strokePaint.StrokeWidth = 2f;
                    canvas.DrawCircle(dotX, dotY, dotSize - 3 * _scale, strokePaint);
                }
            }
        }

        private void DrawIcon(SKCanvas canvas, string iconChar, float x, float y, float size, SKColor color)
        {
            var typeface = _iconTypeface ?? SKTypeface.Default;
            var font = s_iconFont ??= new SKFont(typeface, size);
            font.Size = size;
            font.Typeface = typeface;
            var paint = s_iconPaint ??= new SKPaint { IsAntialias = true };
            paint.Color = color;
            float w = font.MeasureText(iconChar);
            canvas.DrawText(iconChar, x - w / 2, y + size * 0.38f, font, paint);
        }

        private static SKColor ParseHexColor(string hex, byte alpha)
            => RailReader.Core.Services.ColorUtils.ParseHexColor(hex, alpha).ToSKColor();
    }
}
