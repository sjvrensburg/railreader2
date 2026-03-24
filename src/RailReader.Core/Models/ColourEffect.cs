namespace RailReader.Core.Models;

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
    public ColorRGBA Dim { get; init; }
    public bool DimExcludesBlock { get; init; }
    public (ColorRGBA Color, BlendMode BlendMode)? BlockReveal { get; init; }
    public ColorRGBA BlockOutline { get; init; }
    public float BlockOutlineWidth { get; init; }
    public ColorRGBA LineHighlight { get; init; }

    public ColorRGBA ResolveLineHighlight(LineHighlightTint tint, double opacity)
    {
        byte a = (byte)(Math.Clamp(opacity, 0, 1) * 255);
        if (tint == LineHighlightTint.Auto)
            return LineHighlight.WithAlpha(a);
        ColorRGBA baseColor = tint switch
        {
            LineHighlightTint.Yellow => new(255, 220, 50, 255),
            LineHighlightTint.Cyan   => new(0,   255, 255, 255),
            LineHighlightTint.Green  => new(0,   220, 120, 255),
            LineHighlightTint.Pink   => new(255, 130, 180, 255),
            LineHighlightTint.Orange => new(255, 180, 60, 255),
            LineHighlightTint.Blue   => new(100, 160, 255, 255),
            _                        => new(255, 220, 50, 255),
        };
        return baseColor.WithAlpha(a);
    }
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
        _ => effect.ToString(),
    };

    public static OverlayPalette GetOverlayPalette(this ColourEffect effect) => effect switch
    {
        ColourEffect.HighContrast => new OverlayPalette
        {
            Dim = new ColorRGBA(0, 0, 0, 140),
            DimExcludesBlock = true,
            BlockReveal = null,
            BlockOutline = new ColorRGBA(0, 255, 255, 200),
            BlockOutlineWidth = 2.5f,
            LineHighlight = new ColorRGBA(0, 255, 255, 25),
        },
        ColourEffect.HighVisibility => new OverlayPalette
        {
            Dim = new ColorRGBA(0, 0, 0, 120),
            DimExcludesBlock = true,
            BlockReveal = null,
            BlockOutline = new ColorRGBA(255, 230, 0, 200),
            BlockOutlineWidth = 2.5f,
            LineHighlight = new ColorRGBA(255, 230, 0, 30),
        },
        ColourEffect.Amber => new OverlayPalette
        {
            Dim = new ColorRGBA(20, 10, 0, 110),
            DimExcludesBlock = false,
            BlockReveal = (new ColorRGBA(255, 220, 160, 100), BlendMode.Plus),
            BlockOutline = new ColorRGBA(255, 180, 60, 120),
            BlockOutlineWidth = 1.5f,
            LineHighlight = new ColorRGBA(255, 180, 60, 35),
        },
        ColourEffect.Invert => new OverlayPalette
        {
            Dim = new ColorRGBA(60, 60, 60, 100),
            DimExcludesBlock = false,
            BlockReveal = null,
            BlockOutline = new ColorRGBA(0, 220, 120, 180),
            BlockOutlineWidth = 2.0f,
            LineHighlight = new ColorRGBA(0, 220, 120, 40),
        },
        _ => new OverlayPalette // None
        {
            Dim = new ColorRGBA(0, 0, 0, 90),
            DimExcludesBlock = true,
            BlockReveal = null,
            BlockOutline = new ColorRGBA(66, 133, 244, 160),
            BlockOutlineWidth = 1.5f,
            LineHighlight = new ColorRGBA(255, 220, 50, 60),
        },
    };
}
