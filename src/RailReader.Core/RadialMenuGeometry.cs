namespace RailReader.Core;

/// <summary>
/// Pure-geometry helpers for <see cref="RadialMenu"/>. Extracted from the control
/// so the wedge/ring hit-test math can be unit-tested without spinning up an
/// Avalonia visual tree.
/// </summary>
internal static class RadialMenuGeometry
{
    /// <summary>
    /// Given a point (dx, dy) relative to the menu centre and a segment count,
    /// returns the segment index at that angle. Segments are laid out so that
    /// segment 0 is centred at the top (12 o'clock) and subsequent segments
    /// proceed clockwise.
    /// </summary>
    public static int SegmentIndexAt(double dx, double dy, int segmentCount)
    {
        if (segmentCount <= 0) return -1;
        double angle = Math.Atan2(dy, dx);
        if (angle < 0) angle += 2 * Math.PI;
        double segAngle = 2 * Math.PI / segmentCount;
        double offset = -Math.PI / 2 - segAngle / 2;
        double adjusted = angle - offset;
        if (adjusted < 0) adjusted += 2 * Math.PI;
        return (int)(adjusted / segAngle) % segmentCount;
    }

    /// <summary>
    /// Hit-tests against the dots of a sub-ring (colour or thickness) anchored
    /// to <paramref name="segIndex"/>. Returns the index of the hit dot, or -1.
    /// Dots are placed at the mid-radius between <paramref name="innerEdge"/>
    /// and <paramref name="ringRadius"/>, evenly spaced across the segment's
    /// angular extent.
    /// </summary>
    public static int HitTestRingDot(double dx, double dy, int segIndex, int dotCount,
        int segmentCount, double ringRadius, double innerEdge, double hitSize)
    {
        if (segmentCount <= 0 || dotCount <= 0) return -1;
        double segAngle = 2 * Math.PI / segmentCount;
        double startAngle = -Math.PI / 2 - segAngle / 2 + segIndex * segAngle;
        double dotR = (innerEdge + ringRadius) / 2;

        for (int i = 0; i < dotCount; i++)
        {
            double t = (i + 0.5) / dotCount;
            double angle = startAngle + t * segAngle;
            double dotX = dotR * Math.Cos(angle);
            double dotY = dotR * Math.Sin(angle);
            double ddx = dx - dotX, ddy = dy - dotY;
            if (ddx * ddx + ddy * ddy <= hitSize * hitSize)
                return i;
        }
        return -1;
    }
}
