using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable snapshot of all state needed to render the rail overlay for one frame.
/// </summary>
internal sealed record RailOverlayRenderState(
    SKMatrix Camera,
    float PageW,
    float PageH,
    LayoutBlock? CurrentBlock,
    LineInfo CurrentLine,
    bool DebugOverlay,
    PageAnalysis? DebugAnalysis,
    string? DebugModelLabel,
    ColourEffect Effect,
    bool LineFocusBlur,
    bool LineHighlightEnabled,
    float LinePadding,
    LineHighlightTint Tint,
    float TintOpacity);

/// <summary>
/// Hosts a CompositionCustomVisual for the rail overlay (dim, block outline, line highlight).
/// Camera transform is applied inside Skia alongside the draw calls, eliminating
/// the intermediate compositing step that caused jitter on Windows/ANGLE.
/// </summary>
internal class RailOverlayLayer : CompositionLayerControl<RailOverlayVisualHandler>;

internal sealed class RailOverlayVisualHandler : CompositionCustomVisualHandler
{
    private RailOverlayRenderState? _state;

    public override void OnMessage(object message)
    {
        if (message is RailOverlayRenderState state)
        {
            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null) return;

        // No overlay content when rail is inactive and debug is off
        if (state.CurrentBlock is null && !state.DebugOverlay) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();
        canvas.Concat(state.Camera);

        if (state.CurrentBlock is { } block)
        {
            var palette = state.Effect.GetOverlayPalette();
            OverlayRenderer.DrawRailOverlays(
                canvas, block, state.CurrentLine,
                state.PageW, state.PageH, palette,
                state.LineFocusBlur, state.LineHighlightEnabled,
                state.LinePadding, state.Tint, state.TintOpacity,
                OverlayRenderer.GetDimPaint(), OverlayRenderer.GetRevealPaint(),
                OverlayRenderer.GetOutlinePaint(), OverlayRenderer.GetLinePaint());
        }

        if (state.DebugOverlay && state.DebugAnalysis is { } analysis)
        {
            OverlayRenderer.DrawDebugOverlay(
                canvas, analysis,
                OverlayRenderer.GetDebugFont(),
                OverlayRenderer.GetDebugFillPaint(),
                OverlayRenderer.GetDebugStrokePaint(),
                OverlayRenderer.GetDebugBgPaint(),
                OverlayRenderer.GetDebugTextPaint());
        }

        if (state.DebugOverlay && state.DebugModelLabel is { Length: > 0 } modelLabel)
        {
            DrawModelBadge(canvas, modelLabel);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Renders a small "Model: <name>" badge in the top-left of the page so
    /// users can tell at a glance which layout analyzer they're looking at.
    /// Reuses the same Skia primitives the rest of the debug overlay uses
    /// (so the badge inherits any future restyling). Drawn inside the camera
    /// transform, in page coordinates — pinned to (8, 8) in page space.
    /// </summary>
    private static void DrawModelBadge(SKCanvas canvas, string label)
    {
        var text = $"Model: {label}";
        var font = OverlayRenderer.GetDebugFont();
        var textPaint = OverlayRenderer.GetDebugTextPaint();
        var bgPaint = OverlayRenderer.GetDebugBgPaint();

        var width = font.MeasureText(text);
        var metrics = font.Metrics;
        var lineHeight = metrics.Descent - metrics.Ascent;

        const float padX = 6f, padY = 3f;
        const float originX = 8f, originY = 8f;

        var bgRect = new SKRect(
            originX,
            originY,
            originX + width + 2 * padX,
            originY + lineHeight + 2 * padY);

        canvas.DrawRect(bgRect, bgPaint);
        canvas.DrawText(text, originX + padX, originY + padY - metrics.Ascent, font, textPaint);
    }
}
