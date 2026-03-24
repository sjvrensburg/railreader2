using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Conversion helpers between Core-owned types and SkiaSharp types.
/// </summary>
public static class SkiaConversions
{
    public static SKColor ToSKColor(this ColorRGBA c) => new(c.R, c.G, c.B, c.A);

    public static ColorRGBA ToColorRGBA(this SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);

    public static SKRect ToSKRect(this RectF r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static RectF ToRectF(this SKRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static SKBlendMode ToSKBlendMode(this BlendMode m) => m switch
    {
        BlendMode.Plus => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };
}
