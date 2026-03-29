namespace RailReader.Core.Models;

/// <summary>
/// Rendering-library-agnostic rectangle (left/top/right/bottom).
/// </summary>
public record struct RectF(float Left, float Top, float Right, float Bottom)
{
    public readonly float Width => Right - Left;
    public readonly float Height => Bottom - Top;
    public readonly float MidX => (Left + Right) / 2f;
    public readonly float MidY => (Top + Bottom) / 2f;

    public readonly bool Contains(float x, float y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public readonly RectF Inflated(float dx, float dy)
        => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public static RectF Create(float x, float y, float w, float h)
        => new(x, y, x + w, y + h);

    /// <summary>
    /// Returns a normalized rect where Left &lt;= Right and Top &lt;= Bottom.
    /// Useful when coordinates may be swapped (e.g. PDF coordinate conversions).
    /// </summary>
    public readonly RectF Normalized()
        => new(Math.Min(Left, Right), Math.Min(Top, Bottom),
               Math.Max(Left, Right), Math.Max(Top, Bottom));

    public static readonly RectF Empty = new(0, 0, 0, 0);
}
