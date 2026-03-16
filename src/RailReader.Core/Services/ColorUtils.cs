using System.Globalization;
using SkiaSharp;

namespace RailReader.Core.Services;

public static class ColorUtils
{
    /// <summary>
    /// Parses a #RRGGBB hex string to an SKColor with the given alpha.
    /// Falls back to yellow if the format is invalid.
    /// </summary>
    public static SKColor ParseHexColor(string hex, byte alpha)
    {
        if (hex.Length == 7 && hex[0] == '#')
        {
            byte r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);
            return new SKColor(r, g, b, alpha);
        }
        return new SKColor(255, 255, 0, alpha); // fallback yellow
    }
}
