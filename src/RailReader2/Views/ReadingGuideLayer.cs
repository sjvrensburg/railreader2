using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable snapshot for one frame of the reading guide: the line band and (optionally) the word
/// under the pointer, in page space, plus the camera that maps them to the screen. A null
/// <see cref="LineRect"/> means "draw nothing" (mode off, nothing resolved, other page, or rail is
/// drawing its own line visuals).
/// </summary>
internal sealed record ReadingGuideRenderState(
    SKMatrix Camera,
    RectF? LineRect,
    RectF? WordRect,
    float LinePadding,
    SKColor LineColor,
    SKColor WordColor,
    float Zoom)
{
    /// <summary>Screen-space steering indicator, or null when no stick is planted.</summary>
    public SteerIndicator? Steer { get; init; }
}

/// <summary>
/// Hosts a CompositionCustomVisual for the pointer-driven reading guide (docs/reading-guide-design.md):
/// the line band in the configured line-highlight tint, and the word under the pointer in the same tint
/// at a stronger opacity. Two fills, no SaveLayer, no image filters — cheap enough to repaint on every
/// pointer move.
/// </summary>
internal class ReadingGuideLayer : CompositionLayerControl<ReadingGuideVisualHandler>;

internal sealed class ReadingGuideVisualHandler : CompositionCustomVisualHandler
{
    // The word rect must stay legible at low zoom: at 1.25× a 10 pt line is ~12 px, so a tight glyph
    // box can be a few px tall. Pad the word to at least this many DEVICE pixels of height.
    private const float MinWordHeightPx = 10f;
    private const float WordStrokePx = 3f;
    private const float WordCornerPx = 3f;

    private ReadingGuideRenderState? _state;
    private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    public override void OnMessage(object message)
    {
        if (message is ReadingGuideRenderState state)
        {
            _state = state;
            Invalidate();
        }
        else if (message is ReleaseResources)
        {
            _state = null;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null || (state.LineRect is null && state.WordRect is null && state.Steer is null)) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        if (state.LineRect is not null || state.WordRect is not null)
            DrawGuide(canvas, state);
        if (state.Steer is { } steer)
            DrawSteerIndicator(canvas, steer);
    }

    private void DrawGuide(SKCanvas canvas, ReadingGuideRenderState state)
    {
        canvas.Save();
        canvas.Concat(state.Camera);

        // Line band: same geometry as the rail line highlight (OverlayRenderer), padding in line heights.
        // Absent for the rail word marker (§11), where the rail draws the line itself.
        if (state.LineRect is { } line)
        {
            float pad = line.Height * state.LinePadding;
            _paint.Color = state.LineColor;
            canvas.DrawRect(SKRect.Create(line.Left, line.Top - pad, line.Width, line.Height + pad * 2), _paint);
        }

        if (state.WordRect is { } word)
        {
            // A thin rounded OUTLINE around the word, not a fill: a fill over the glyphs cost too much
            // contrast for the readers this is for (first rail test). Stroke width in device px.
            float zoom = state.Zoom > 0 ? state.Zoom : 1f;
            float minH = MinWordHeightPx / zoom;          // device px → page units
            float grow = Math.Max(0f, (minH - word.Height) / 2f);
            float hpad = word.Height * 0.12f + WordStrokePx / zoom;   // clear of the glyphs
            float vpad = word.Height * 0.08f + grow;
            var rect = SKRect.Create(word.Left - hpad, word.Top - vpad, word.Width + hpad * 2, word.Height + vpad * 2);
            _paint.Color = state.WordColor;
            _paint.Style = SKPaintStyle.Stroke;
            _paint.StrokeWidth = WordStrokePx / zoom;
            float r = WordCornerPx / zoom;
            canvas.DrawRoundRect(rect, r, r, _paint);
            _paint.Style = SKPaintStyle.Fill;
        }

        canvas.Restore();
    }

    // Steering indicator: a faint square marking the still zone where Shift went down, a knob where the
    // (hidden) pointer is, and a line between them once pushed past the zone. The zone is a SQUARE because
    // Core's ReadingGuideSteer measures displacement as max(|dx|, |dy|) — a diagonal nudge that is still
    // inside the square must not look like a push. Each stroke sits on a white halo so it reads on both a
    // white page and a dark/inverted one. Deliberately small and quiet — it is orientation, not a
    // control; the knob firms up as the throttle rises. No closures: this redraws every frame.
    private static readonly SKColor SteerAccent = new(0x33, 0x90, 0xFF);
    private const float KnobRadiusPx = 6f;
    private const float ZoneCornerPx = 8f;

    private void DrawSteerIndicator(SKCanvas canvas, SteerIndicator st)
    {
        float ox = (float)st.OriginX, oy = (float)st.OriginY;
        float dx = (float)(st.PointerX - st.OriginX), dy = (float)(st.PointerY - st.OriginY);
        float dead = (float)st.DeadZonePx;
        float full = (float)st.FullDeflectionPx;
        float mag = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));   // Core's norm
        if (mag > full && mag > 0) { float k = full / mag; dx *= k; dy *= k; mag = full; }
        float kx = ox + dx, ky = oy + dy;
        bool pushing = mag > dead;
        byte strong = (byte)(150 + 90 * Math.Clamp(st.Throttle, 0, 1));
        var zone = SKRect.Create(ox - dead, oy - dead, dead * 2, dead * 2);

        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeCap = SKStrokeCap.Round;
        for (int pass = 0; pass < 2; pass++)   // pass 0: white halo, pass 1: accent
        {
            bool halo = pass == 0;
            // Still zone.
            Stroke(halo, 1.5f, SteerAccent.WithAlpha(110));
            canvas.DrawRoundRect(zone, ZoneCornerPx, ZoneCornerPx, _paint);
            if (pushing)
            {
                // Push line from where it leaves the zone to the knob.
                float t = dead / mag;
                Stroke(halo, 2f, SteerAccent.WithAlpha(strong));
                canvas.DrawLine(ox + dx * t, oy + dy * t, kx, ky, _paint);
            }
            else
            {
                // Hollow knob inside the zone.
                Stroke(halo, 2f, SteerAccent.WithAlpha(200));
                canvas.DrawCircle(kx, ky, KnobRadiusPx, _paint);
            }
        }
        if (pushing)
        {
            // Filled knob once the stick is pushing.
            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = SKColors.White.WithAlpha(200);
            canvas.DrawCircle(kx, ky, KnobRadiusPx + 2f, _paint);
            _paint.Color = SteerAccent.WithAlpha(strong);
            canvas.DrawCircle(kx, ky, KnobRadiusPx, _paint);
        }
        _paint.Style = SKPaintStyle.Fill;
    }

    private void Stroke(bool halo, float width, SKColor color)
    {
        _paint.StrokeWidth = halo ? width + 3f : width;
        _paint.Color = halo ? SKColors.White.WithAlpha(150) : color;
    }
}
