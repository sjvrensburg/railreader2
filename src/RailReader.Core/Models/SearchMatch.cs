namespace RailReader.Core.Models;

public record SearchMatch(int PageIndex, int CharStart, int CharLength, List<RectF> Rects);
