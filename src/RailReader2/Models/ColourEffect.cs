using SkiaSharp;

namespace RailReader2.Models;

public enum ColourEffect
{
    None,
    HighContrast,
    HighVisibility,
    Amber,
    Invert
}

public sealed class OverlayPalette
{
    public SKColor Dim { get; init; }
    public bool DimExcludesBlock { get; init; }
    public (SKColor Color, SKBlendMode BlendMode)? BlockReveal { get; init; }
    public SKColor BlockOutline { get; init; }
    public float BlockOutlineWidth { get; init; }
    public SKColor LineHighlight { get; init; }
}

public static class ColourEffectExtensions
{
    public static readonly (ColourEffect Effect, string Description)[] All =
    [
        (ColourEffect.None, "No colour effect"),
        (ColourEffect.HighContrast, "White on black for glare reduction"),
        (ColourEffect.HighVisibility, "Yellow on black for maximum legibility"),
        (ColourEffect.Amber, "Warm amber tint for haze reduction"),
        (ColourEffect.Invert, "Invert colours for eye strain relief"),
    ];

    public static string DisplayName(this ColourEffect effect) => effect switch
    {
        ColourEffect.None => "None",
        ColourEffect.HighContrast => "High Contrast",
        ColourEffect.HighVisibility => "High Visibility",
        ColourEffect.Amber => "Amber Filter",
        ColourEffect.Invert => "Invert",
        _ => effect.ToString()
    };

    public static OverlayPalette GetOverlayPalette(this ColourEffect effect) => effect switch
    {
        ColourEffect.HighContrast => new OverlayPalette
        {
            Dim = new SKColor(0, 0, 0, 140),
            DimExcludesBlock = true,
            BlockReveal = null,
            BlockOutline = new SKColor(0, 255, 255, 200),
            BlockOutlineWidth = 2.5f,
            LineHighlight = new SKColor(0, 255, 255, 25),
        },
        ColourEffect.HighVisibility => new OverlayPalette
        {
            Dim = new SKColor(0, 0, 0, 120),
            DimExcludesBlock = true,
            BlockReveal = null,
            BlockOutline = new SKColor(255, 230, 0, 200),
            BlockOutlineWidth = 2.5f,
            LineHighlight = new SKColor(255, 230, 0, 30),
        },
        ColourEffect.Amber => new OverlayPalette
        {
            Dim = new SKColor(20, 10, 0, 110),
            DimExcludesBlock = false,
            BlockReveal = (new SKColor(255, 220, 160, 100), SKBlendMode.Plus),
            BlockOutline = new SKColor(255, 180, 60, 120),
            BlockOutlineWidth = 1.5f,
            LineHighlight = new SKColor(255, 180, 60, 35),
        },
        ColourEffect.Invert => new OverlayPalette
        {
            Dim = new SKColor(60, 60, 60, 100),
            DimExcludesBlock = false,
            BlockReveal = null,
            BlockOutline = new SKColor(0, 220, 120, 180),
            BlockOutlineWidth = 2.0f,
            LineHighlight = new SKColor(0, 220, 120, 40),
        },
        _ => new OverlayPalette // None
        {
            Dim = new SKColor(0, 0, 0, 120),
            DimExcludesBlock = false,
            BlockReveal = (new SKColor(255, 255, 255, 120), SKBlendMode.Plus),
            BlockOutline = new SKColor(66, 133, 244, 80),
            BlockOutlineWidth = 1.5f,
            LineHighlight = new SKColor(66, 133, 244, 40),
        },
    };
}
