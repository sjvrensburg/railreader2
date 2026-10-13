using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
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
    float Zoom);

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
        if (state is null || (state.LineRect is null && state.WordRect is null)) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

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
            float zoom = state.Zoom > 0 ? state.Zoom : 1f;
            float minH = MinWordHeightPx / zoom;          // device px → page units
            float grow = Math.Max(0f, (minH - word.Height) / 2f);
            float hpad = word.Height * 0.08f;             // a hair of side padding so the fill hugs the glyphs
            var rect = SKRect.Create(word.Left - hpad, word.Top - grow, word.Width + hpad * 2, word.Height + grow * 2);
            _paint.Color = state.WordColor;
            float r = WordCornerPx / zoom;
            canvas.DrawRoundRect(rect, r, r, _paint);
        }

        canvas.Restore();
    }
}
