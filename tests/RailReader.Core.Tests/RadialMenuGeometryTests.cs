using RailReader.Core;
using Xunit;

namespace RailReader.Core.Tests;

public class RadialMenuGeometryTests
{
    // --- SegmentIndexAt ---

    [Fact]
    public void SegmentIndexAt_StraightUp_ReturnsSegmentZero()
    {
        // dy negative = up (screen coords). Segment 0 centred at 12 o'clock.
        int idx = RadialMenuGeometry.SegmentIndexAt(dx: 0, dy: -100, segmentCount: 4);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void SegmentIndexAt_Right_ReturnsSegmentOneForFourSegments()
    {
        int idx = RadialMenuGeometry.SegmentIndexAt(dx: 100, dy: 0, segmentCount: 4);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void SegmentIndexAt_Down_ReturnsSegmentTwoForFourSegments()
    {
        int idx = RadialMenuGeometry.SegmentIndexAt(dx: 0, dy: 100, segmentCount: 4);
        Assert.Equal(2, idx);
    }

    [Fact]
    public void SegmentIndexAt_Left_ReturnsSegmentThreeForFourSegments()
    {
        int idx = RadialMenuGeometry.SegmentIndexAt(dx: -100, dy: 0, segmentCount: 4);
        Assert.Equal(3, idx);
    }

    [Fact]
    public void SegmentIndexAt_ZeroSegments_ReturnsNegativeOne()
    {
        int idx = RadialMenuGeometry.SegmentIndexAt(dx: 0, dy: -100, segmentCount: 0);
        Assert.Equal(-1, idx);
    }

    [Fact]
    public void SegmentIndexAt_SixSegments_FullRotationStaysInRange()
    {
        for (int deg = 0; deg < 360; deg += 15)
        {
            double rad = deg * Math.PI / 180;
            double dx = Math.Cos(rad) * 50;
            double dy = Math.Sin(rad) * 50;
            int idx = RadialMenuGeometry.SegmentIndexAt(dx, dy, segmentCount: 6);
            Assert.InRange(idx, 0, 5);
        }
    }

    // --- HitTestRingDot ---

    [Fact]
    public void HitTestRingDot_ExactlyOnDotCentre_Hits()
    {
        // Single-segment menu with one dot: dot centre is at the segment midline
        // between innerEdge and ringRadius. For seg 0 (top), that's straight up.
        int idx = RadialMenuGeometry.HitTestRingDot(
            dx: 0, dy: -75, segIndex: 0, dotCount: 1, segmentCount: 1,
            ringRadius: 100, innerEdge: 50, hitSize: 9);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void HitTestRingDot_OutsideHitRadius_Misses()
    {
        int idx = RadialMenuGeometry.HitTestRingDot(
            dx: 200, dy: -75, segIndex: 0, dotCount: 1, segmentCount: 1,
            ringRadius: 100, innerEdge: 50, hitSize: 9);
        Assert.Equal(-1, idx);
    }

    [Fact]
    public void HitTestRingDot_WithinHitRadius_Hits()
    {
        int idx = RadialMenuGeometry.HitTestRingDot(
            dx: 5, dy: -73, segIndex: 0, dotCount: 1, segmentCount: 1,
            ringRadius: 100, innerEdge: 50, hitSize: 9);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void HitTestRingDot_MultipleDots_ReturnsCorrectIndex()
    {
        // Two-dot ring on a four-segment menu, segment 0 (top). Each dot should
        // be hittable at its own position.
        const int segs = 4, dots = 2;
        const double ring = 100, inner = 50;
        double dotR = (inner + ring) / 2; // 75
        double segAngle = 2 * Math.PI / segs;
        double startAngle = -Math.PI / 2 - segAngle / 2;

        for (int d = 0; d < dots; d++)
        {
            double t = (d + 0.5) / dots;
            double angle = startAngle + t * segAngle;
            double dx = dotR * Math.Cos(angle);
            double dy = dotR * Math.Sin(angle);

            int idx = RadialMenuGeometry.HitTestRingDot(
                dx, dy, segIndex: 0, dotCount: dots, segmentCount: segs,
                ringRadius: ring, innerEdge: inner, hitSize: 5);
            Assert.Equal(d, idx);
        }
    }

    [Fact]
    public void HitTestRingDot_ZeroDots_ReturnsNegativeOne()
    {
        int idx = RadialMenuGeometry.HitTestRingDot(
            dx: 0, dy: -75, segIndex: 0, dotCount: 0, segmentCount: 1,
            ringRadius: 100, innerEdge: 50, hitSize: 9);
        Assert.Equal(-1, idx);
    }

    [Fact]
    public void HitTestRingDot_ZeroSegments_ReturnsNegativeOne()
    {
        int idx = RadialMenuGeometry.HitTestRingDot(
            dx: 0, dy: -75, segIndex: 0, dotCount: 1, segmentCount: 0,
            ringRadius: 100, innerEdge: 50, hitSize: 9);
        Assert.Equal(-1, idx);
    }
}
