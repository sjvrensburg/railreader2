using SkiaSharp;

namespace RailReader2.Models;

public record SearchMatch(int PageIndex, int CharStart, int CharLength, List<SKRect> Rects);
