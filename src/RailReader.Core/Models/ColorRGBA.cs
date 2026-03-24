namespace RailReader.Core.Models;

/// <summary>
/// Rendering-library-agnostic RGBA colour.
/// </summary>
public record struct ColorRGBA(byte R, byte G, byte B, byte A)
{
    public readonly ColorRGBA WithAlpha(byte a) => new(R, G, B, a);
}

/// <summary>
/// Rendering-library-agnostic blend mode (subset used by overlay palette).
/// </summary>
public enum BlendMode
{
    SrcOver,
    Plus,
}
