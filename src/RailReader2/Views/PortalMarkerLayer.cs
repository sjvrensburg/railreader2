using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Screen-space geometry for portal markers, shared by the drawing handler and the
/// <see cref="ViewportPanel"/> hit-test so both agree on where a marker sits. Markers are a fixed
/// pixel size (they don't scale with zoom): each page-space anchor is mapped to screen, then the glyph
/// is drawn/hit-tested at a constant radius there.
/// </summary>
internal static class PortalMarkerGeometry
{
    public const float Radius = 7f;            // marker glyph radius, screen px
    public const double SourceGutterGap = 13;  // source marker centre sits this far left of the block edge
    public const double HitRadius = Radius + 5; // generous click target

    /// <summary>Screen-space centre of a marker given its page anchor and the camera (page→screen is
    /// <c>screen = page·zoom + offset</c>). Source markers sit in the left gutter at the line; target
    /// markers straddle the block's top-right corner.</summary>
    public static (double X, double Y) ScreenCentre(bool isSource, double pageX, double pageY,
        double zoom, double offsetX, double offsetY)
    {
        double sx = pageX * zoom + offsetX;
        double sy = pageY * zoom + offsetY;
        return isSource ? (sx - SourceGutterGap, sy) : (sx, sy);
    }
}

/// <summary>One marker to draw: a source gutter circle or a target corner badge.</summary>
internal readonly record struct PortalMarkerInfo(bool IsSource, double PageX, double PageY, bool IsActive, int Count);

/// <summary>Immutable per-frame snapshot for the portal marker overlay. The camera maps each marker's
/// page anchor to screen; the glyphs themselves are drawn at a fixed pixel size.</summary>
internal sealed record PortalMarkerRenderState(SKMatrix Camera, IReadOnlyList<PortalMarkerInfo> Markers);

/// <summary>Hosts a CompositionCustomVisual that draws the always-on portal markers on top of the page.</summary>
internal class PortalMarkerLayer : CompositionLayerControl<PortalMarkerVisualHandler>;

internal sealed class PortalMarkerVisualHandler : CompositionCustomVisualHandler
{
    private const float Radius = PortalMarkerGeometry.Radius;

    // Accent (active / currently-pinned portal) vs muted (everything else). A white halo + thin dark
    // ring give contrast on both light and dark figures.
    private static readonly SKColor Accent = new(0x2D, 0x7D, 0xD2);
    private static readonly SKColor Muted = new(0x6B, 0x6B, 0x6B);
    private static readonly SKColor Halo = new(0xFF, 0xFF, 0xFF, 0xCC);
    private static readonly SKColor Ring = new(0x00, 0x00, 0x00, 0x66);
    private static readonly SKColor Glyph = new(0xFF, 0xFF, 0xFF, 0xF0);

    private PortalMarkerRenderState? _state;

    public override void OnMessage(object message)
    {
        if (message is PortalMarkerRenderState state)
        {
            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state?.Markers is not { Count: > 0 } markers) return;
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        double zoom = state.Camera.ScaleX, ox = state.Camera.TransX, oy = state.Camera.TransY;

        using var halo = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = Halo, IsAntialias = true };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var ring = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = Ring, IsAntialias = true };
        using var glyph = new SKPaint { Style = SKPaintStyle.Fill, Color = Glyph, IsAntialias = true };
        using var font = new SKFont { Size = Radius * 1.25f, Embolden = true };

        foreach (var m in markers)
        {
            var (cx, cy) = PortalMarkerGeometry.ScreenCentre(m.IsSource, m.PageX, m.PageY, zoom, ox, oy);
            float fx = (float)cx, fy = (float)cy;
            fill.Color = (m.IsActive ? Accent : Muted).WithAlpha(m.IsActive ? (byte)0xF0 : (byte)0xB0);

            if (m.IsSource)
            {
                canvas.DrawCircle(fx, fy, Radius, halo);
                canvas.DrawCircle(fx, fy, Radius, fill);
                canvas.DrawCircle(fx, fy, Radius, ring);
            }
            else
            {
                var rect = new SKRect(fx - Radius, fy - Radius, fx + Radius, fy + Radius);
                canvas.DrawRoundRect(rect, 2.5f, 2.5f, halo);
                canvas.DrawRoundRect(rect, 2.5f, 2.5f, fill);
                canvas.DrawRoundRect(rect, 2.5f, 2.5f, ring);
            }

            if (m.Count > 1)
                canvas.DrawText(m.Count.ToString(), fx, fy + Radius * 0.55f, SKTextAlign.Center, font, glyph);
            else
                canvas.DrawCircle(fx, fy, Radius * 0.34f, glyph);
        }
    }
}
