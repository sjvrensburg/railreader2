using System.Globalization;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public static class ColorUtils
{
    /// <summary>
    /// Parses a #RRGGBB hex string to a ColorRGBA with the given alpha.
    /// Falls back to yellow if the format is invalid.
    /// </summary>
    public static ColorRGBA ParseHexColor(string hex, byte alpha)
    {
        if (hex.Length == 7 && hex[0] == '#')
        {
            byte r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);
            return new ColorRGBA(r, g, b, alpha);
        }
        return new ColorRGBA(255, 255, 0, alpha); // fallback yellow
    }
}
