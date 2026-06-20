using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable per-frame snapshot for the table freeze-panes overlay. The three crops (corner /
/// frozen-top / frozen-left, any may be null) are pre-rendered page-region images; their destination
/// rectangles are already in *screen* space (computed on the UI thread from the camera), so the
/// handler draws them directly without a camera concat — like <see cref="PortalMarkerLayer"/>.
/// </summary>
internal sealed record FreezePaneRenderState(
    SKImage? Corner, SKImage? Top, SKImage? Left,
    SKRect CornerDst, SKRect TopDst, SKRect LeftDst,
    ColourEffect Effect, float EffectIntensity, ColourEffectShaders? Effects);

/// <summary>Hosts a CompositionCustomVisual that draws Excel-style frozen table panes (the rows above
/// and columns left of the frozen cell) pinned over the live page while rail-reading a table.</summary>
internal class FreezePaneLayer : CompositionLayerControl<FreezePaneVisualHandler>;

internal sealed class FreezePaneVisualHandler : CompositionCustomVisualHandler
{
    // Crisp resampling at rest (the tiles are scaled to the live zoom). Matches PdfPageLayer's
    // at-rest sampling so a frozen header reads identically to the live header.
    private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);

    // Cache the colour-effect filter so frozen tiles match the page under invert/amber/dark without
    // re-creating the filter every frame. Keyed by effect + intensity, like PdfPageVisualHandler.
    [ThreadStatic] private static SKColorFilter? s_effectFilter;
    [ThreadStatic] private static ColourEffect s_effectType;
    [ThreadStatic] private static float s_effectIntensity;
    [ThreadStatic] private static SKPaint? s_paint;

    private FreezePaneRenderState? _state;

    public override void OnMessage(object message)
    {
        if (message is RetireImage retire)
        {
            // Dispose a replaced/cleared crop on the composition thread, where OnRender is not
            // concurrently accessing it (the new state — without this image — was already sent).
            retire.Image.Dispose();
            return;
        }
        if (message is FreezePaneRenderState state)
        {
            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null) return;
        if (state.Corner is null && state.Top is null && state.Left is null) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        // Match the live page's colour effect so a frozen tile doesn't read as a brighter/un-tinted patch.
        SKColorFilter? effectFilter = null;
        if (state.Effects?.HasActiveEffect(state.Effect) == true)
        {
            if (s_effectFilter is null || s_effectType != state.Effect
                || System.Math.Abs(s_effectIntensity - state.EffectIntensity) > 0.001f)
            {
                s_effectFilter?.Dispose();
                s_effectFilter = state.Effects.CreateColorFilter(state.Effect, state.EffectIntensity);
                s_effectType = state.Effect;
                s_effectIntensity = state.EffectIntensity;
            }
            effectFilter = s_effectFilter;
        }

        s_paint ??= new SKPaint();
        s_paint.ColorFilter = effectFilter;

        // Draw order: top band, then left band, then corner on top — the corner occludes the shared
        // top-left overlap and the seam where the live body slides under the frozen panes.
        if (state.Top is { } top)
            canvas.DrawImage(top, SKRect.Create(top.Width, top.Height), state.TopDst, s_sampling, s_paint);
        if (state.Left is { } left)
            canvas.DrawImage(left, SKRect.Create(left.Width, left.Height), state.LeftDst, s_sampling, s_paint);
        if (state.Corner is { } corner)
            canvas.DrawImage(corner, SKRect.Create(corner.Width, corner.Height), state.CornerDst, s_sampling, s_paint);

        // Don't let the cached paint retain a filter that may be disposed before the next frame.
        s_paint.ColorFilter = null;
    }
}
